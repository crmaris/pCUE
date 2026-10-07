#!/usr/bin/env python3
"""Unattended pCUE Linux updater. HTTPS + SHA256; no downgrades or active-app replacement."""
import argparse
import contextlib
import fcntl
import hashlib
import json
import os
import pathlib
import re
import shutil
import subprocess
import tarfile
import tempfile
import time
import urllib.parse
import urllib.request

MANIFEST = "https://raw.githubusercontent.com/crmaris/powenetics-updates/main/components.json"
LOCK = pathlib.Path("/run/lock/pcue-linux.update.lock")
MAX_BYTES = 256 * 1024 * 1024


def version(value):
    if not isinstance(value, str) or not re.fullmatch(r"\d+\.\d+\.\d+", value):
        raise ValueError("Invalid release version")
    return tuple(int(n) for n in value.split("."))


def release_entry(document, installed):
    entry = document["apps"]["pcue-linux"]
    if version(entry["version"]) <= version(installed):
        return None
    for url_key, hash_key in [("url", "sha256"), ("portableUrl", "portableSha256")]:
        parsed = urllib.parse.urlsplit(entry[url_key])
        if parsed.scheme != "https" or parsed.netloc != "github.com" or not parsed.path.startswith("/crmaris/pCUE/releases/download/") or parsed.query or parsed.fragment:
            raise ValueError("Release URL is outside the approved public pCUE repository")
        if not re.fullmatch(r"[0-9a-fA-F]{64}", entry[hash_key]):
            raise ValueError("A valid SHA256 is required")
    return entry


def download(url, target, expected_hash):
    parsed = urllib.parse.urlsplit(url)
    if parsed.scheme != "https":
        raise ValueError("HTTPS is required")
    request = urllib.request.Request(url, headers={"User-Agent": "pCUE-Linux-Updater"})
    digest, size = hashlib.sha256(), 0
    with urllib.request.urlopen(request, timeout=45) as response, target.open("xb") as output:
        final = urllib.parse.urlsplit(response.url)
        if final.scheme != "https" or final.netloc not in {"github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"}:
            raise ValueError("Unapproved release redirect")
        while chunk := response.read(1024 * 1024):
            size += len(chunk)
            if size > MAX_BYTES:
                raise ValueError("Release exceeds size limit")
            digest.update(chunk)
            output.write(chunk)
        output.flush()
        os.fsync(output.fileno())
    if digest.hexdigest().lower() != expected_hash.lower():
        target.unlink()
        raise ValueError("Release checksum mismatch")


def extract_portable(archive, destination, release_version):
    prefix = "pcue-linux-" + release_version + "/"
    with tarfile.open(archive, "r:gz") as tar:
        members = tar.getmembers()
        if len(members) > 10000 or sum(item.size for item in members) > 512 * 1024 * 1024:
            raise ValueError("Portable archive exceeds limits")
        names = set()
        for item in members:
            path = pathlib.PurePosixPath(item.name)
            if not item.isfile() or not item.name.startswith(prefix) or ".." in path.parts or path.is_absolute() or "\\" in item.name:
                raise ValueError("Unsafe portable archive entry")
            relative = item.name[len(prefix):]
            if not relative or pathlib.PurePosixPath(relative).is_absolute() or relative in names:
                raise ValueError("Duplicate portable archive entry")
            names.add(relative)
        for item in members:
            path = destination / item.name[len(prefix):]
            path.parent.mkdir(parents=True, exist_ok=True)
            with tar.extractfile(item) as source, path.open("xb") as output:
                shutil.copyfileobj(source, output)
            path.chmod(0o755 if path.name == "pcue-linux" else 0o644)
    app = destination / "pcue-linux"
    if not app.is_file() or app.read_bytes()[:4] != b"\x7fELF" or not (destination / "libcoreclr.so").is_file():
        raise ValueError("Portable runtime is incomplete")


def fetch_entry(installed):
    request = urllib.request.Request(MANIFEST, headers={"User-Agent": "pCUE-Linux-Updater", "Cache-Control": "no-cache"})
    with urllib.request.urlopen(request, timeout=20) as response:
        if urllib.parse.urlsplit(response.url).netloc != "raw.githubusercontent.com":
            raise ValueError("Unapproved manifest redirect")
        raw = response.read(1024 * 1024 + 1)
        if len(raw) > 1024 * 1024:
            raise ValueError("Manifest exceeds limit")
    return release_entry(json.loads(raw), installed)


def update_deb():
    if os.geteuid() != 0:
        raise PermissionError("The installed-package updater runs through its systemd service")
    with LOCK.open("rb") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            print("pCUE is running; update deferred")
            return
        if any(pathlib.Path("/var/lib/pcue-linux/recovery").glob("recovery-*")):
            print("Saved controller recovery is pending; update deferred")
            return
        installed = subprocess.check_output(["dpkg-query", "-W", "-f=${Version}", "pcue-linux"], text=True).strip()
        entry = fetch_entry(installed)
        if not entry:
            print("pCUE Linux is current")
            return
        with tempfile.TemporaryDirectory(prefix="pcue-linux-update-", dir="/var/tmp") as directory:
            package = pathlib.Path(directory) / "pcue-linux.deb"
            download(entry["url"], package, entry["sha256"])
            fields = subprocess.check_output(["dpkg-deb", "-f", str(package), "Package", "Version", "Architecture"], text=True)
            expected = {"Package": "pcue-linux", "Version": entry["version"], "Architecture": "amd64"}
            parsed = dict(line.split(": ", 1) for line in fields.strip().splitlines())
            if parsed != expected:
                raise ValueError("Debian package identity differs from the release")
            environment = dict(os.environ, DEBIAN_FRONTEND="noninteractive")
            subprocess.run(["apt-get", "-y", "-o", "DPkg::Lock::Timeout=30", "install", str(package)], check=True, env=environment, timeout=300)


def update_portable(destination, installed, parent_pid, expected_release=None, ready=None):
    destination = destination.resolve(strict=True)
    if destination == pathlib.Path("/opt/pcue-linux") or os.geteuid() == 0 or not os.access(destination.parent, os.W_OK):
        raise PermissionError("Portable automatic updates require a user-owned writable directory")
    entry = fetch_entry(installed)
    if not entry or (expected_release and entry["version"] != expected_release):
        return
    with tempfile.TemporaryDirectory(prefix=".pcue-linux-update-", dir=destination.parent) as directory:
        scratch = pathlib.Path(directory)
        archive = scratch / "update.tar.gz"
        download(entry["portableUrl"], archive, entry["portableSha256"])
        stage = scratch / "stage"
        stage.mkdir()
        extract_portable(archive, stage, entry["version"])
        reported = subprocess.check_output([str(stage / "pcue-linux"), "--version"], text=True, timeout=15).strip()
        if reported != "pCUE Linux " + entry["version"]:
            raise ValueError("Staged executable version differs from the release")
        if ready:
            ready.write_text("verified", encoding="ascii")
        # The GUI stops and releases hardware before exiting. Never kill it here.
        for _ in range(60):
            if not pathlib.Path(f"/proc/{parent_pid}").exists():
                break
            time.sleep(1)
        else:
            raise TimeoutError("pCUE did not exit; portable update cancelled")
        backup = scratch / "previous"
        os.replace(destination, backup)
        try:
            os.replace(stage, destination)
            subprocess.Popen([str(destination / "pcue-linux")], start_new_session=True)
        except Exception:
            if destination.exists():
                shutil.rmtree(destination)
            os.replace(backup, destination)
            raise


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--deb", action="store_true")
    parser.add_argument("--portable", type=pathlib.Path)
    parser.add_argument("--installed")
    parser.add_argument("--parent-pid", type=int)
    parser.add_argument("--expected-release")
    parser.add_argument("--ready", type=pathlib.Path)
    args = parser.parse_args()
    try:
        if args.deb:
            update_deb()
        elif args.portable and args.installed and args.parent_pid and args.expected_release:
            update_portable(args.portable, args.installed, args.parent_pid, args.expected_release, args.ready)
        else:
            parser.error("Choose --deb or a complete portable update request")
    except Exception as error:
        print("pCUE Linux update deferred: " + str(error))
        raise SystemExit(1)
