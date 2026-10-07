"""Build a portable archive and Debian package from an already published linux-x64 tree.

No SDK, root permissions or Linux host is required to assemble the archives. Validation on
Linux is separate. Refuse to overwrite any prior deliverable, including its checksum.
"""
import argparse
import gzip
import hashlib
import io
import json
import pathlib
import re
import tarfile


def archive(entries, epoch):
    output = io.BytesIO()
    with gzip.GzipFile(fileobj=output, mode="wb", mtime=epoch, filename="") as compressed:
        with tarfile.open(fileobj=compressed, mode="w", format=tarfile.PAX_FORMAT) as tar:
            for name, data, mode in sorted(entries):
                item = tarfile.TarInfo(name)
                item.size, item.mode, item.mtime = len(data), mode, epoch
                item.uid = item.gid = 0
                item.uname = item.gname = "root"
                tar.addfile(item, io.BytesIO(data))
    return output.getvalue()


def ar_member(name, data, epoch):
    header = f"{name + '/':<16}{epoch:<12}{0:<6}{0:<6}{'100644':<8}{len(data):<10}`\n".encode("ascii")
    if len(header) != 60:
        raise ValueError("Invalid ar member header")
    return header + data + (b"\n" if len(data) % 2 else b"")


def build(publish, destination, version, epoch):
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise ValueError("Use the three-part version stamped by the Windows release pack")
    destination.mkdir(parents=True, exist_ok=True)
    names = [f"pCUE_{version}_linux-x64.tar.gz", f"pcue-linux_{version}_amd64.deb"]
    paths = [destination / name for name in names]
    if any(p.exists() or pathlib.Path(str(p) + ".sha256").exists() for p in paths):
        raise FileExistsError("A Linux deliverable or checksum already exists; it will not be replaced")
    app = publish / "pcue-linux"
    if not app.is_file() or app.read_bytes()[:4] != b"\x7fELF":
        raise ValueError("The published apphost is not a Linux ELF executable")
    if not (publish / "libcoreclr.so").is_file():
        raise ValueError("The self-contained Linux runtime is missing")
    root = pathlib.Path(__file__).resolve().parent.parent
    payload = []
    for file in sorted(publish.rglob("*")):
        if file.is_file():
            mode = 0o755 if file.name == "pcue-linux" else 0o644
            payload.append((file.relative_to(publish).as_posix(), file.read_bytes(), mode))
    docs = [root / "LINUX.md", root / "shared" / "LICENSE", root / "linux" / "THIRD-PARTY.md"]
    for file in docs:
        payload.append((file.name, file.read_bytes(), 0o644))
    for file in sorted((root / "linux/licenses").rglob("*")):
        if file.is_file():
            payload.append(("licenses/" + file.relative_to(root / "linux/licenses").as_posix(), file.read_bytes(), 0o644))
    packaging = root / "linux" / "packaging"
    rule = (packaging / "70-pcue-linux.rules").read_bytes()
    payload.append(("update-linux.py", (packaging / "update-linux.py").read_bytes(), 0o644))
    portable = [(f"pcue-linux-{version}/{name}", data, mode) for name, data, mode in payload]
    portable.append((f"pcue-linux-{version}/70-pcue-linux.rules", rule, 0o644))
    portable_bytes = archive(portable, epoch)
    deb_data = [(f"opt/pcue-linux/{name}", data, mode) for name, data, mode in payload]
    deb_data += [
        ("usr/bin/pcue-linux", b'#!/bin/sh\nexec /opt/pcue-linux/pcue-linux "$@"\n', 0o755),
        ("usr/share/applications/pcue-linux.desktop", (packaging / "pcue-linux.desktop").read_bytes(), 0o644),
        ("usr/share/icons/hicolor/scalable/apps/pcue-linux.svg", (packaging / "pcue-linux.svg").read_bytes(), 0o644),
        ("usr/lib/udev/rules.d/70-pcue-linux.rules", rule, 0o644),
        ("usr/lib/systemd/system/pcue-linux-update.service", (packaging / "pcue-linux-update.service").read_bytes(), 0o644),
        ("usr/lib/systemd/system/pcue-linux-update.timer", (packaging / "pcue-linux-update.timer").read_bytes(), 0o644),
        ("usr/lib/tmpfiles.d/pcue-linux.conf", (packaging / "pcue-linux.conf").read_bytes(), 0o644),
    ]
    installed_size = (sum(len(data) for _, data, _ in deb_data) + 1023) // 1024
    control = f"""Package: pcue-linux
Version: {version}
Architecture: amd64
Maintainer: pCUE maintainers <https://github.com/crmaris/pCUE>
Section: utils
Priority: optional
Installed-Size: {installed_size}
Depends: libc6 (>= 2.35), libgcc-s1, libstdc++6, libssl3 | libssl3t64, libicu70 | libicu72 | libicu74 | libicu76 | libicu78, libfontconfig1, libx11-6, libice6, libsm6, libudev1, zlib1g, python3, systemd, apt
Homepage: https://github.com/crmaris/pCUE
Description: Linux desktop fan and pump controls
 Self-contained pCUE desktop for Commander PRO, CORE/XT, LINK and OCTO.
 USB control is experimental; hardware validation is still required.
""".encode()
    postinst = b'#!/bin/sh\nset -e\nsystemd-tmpfiles --create /usr/lib/tmpfiles.d/pcue-linux.conf\nif command -v udevadm >/dev/null 2>&1; then udevadm control --reload-rules || true; fi\nif [ -d /run/systemd/system ]; then\n  systemctl daemon-reload\n  systemctl enable --now pcue-linux-update.timer\nfi\nexit 0\n'
    prerm = b'#!/bin/sh\nset -e\nif [ "$1" = remove ] && [ -d /run/systemd/system ]; then systemctl disable --now pcue-linux-update.timer; fi\nexit 0\n'
    postrm = b'#!/bin/sh\nset -e\nif [ -d /run/systemd/system ]; then systemctl daemon-reload; fi\nif command -v udevadm >/dev/null 2>&1; then udevadm control --reload-rules || true; fi\nexit 0\n'
    control_bytes = archive([("control", control, 0o644), ("postinst", postinst, 0o755), ("prerm", prerm, 0o755), ("postrm", postrm, 0o755)], epoch)
    deb_bytes = b"!<arch>\n" + ar_member("debian-binary", b"2.0\n", epoch) + ar_member("control.tar.gz", control_bytes, epoch) + ar_member("data.tar.gz", archive(deb_data, epoch), epoch)
    results = []
    for path, data in zip(paths, [portable_bytes, deb_bytes]):
        with path.open("xb") as f:
            f.write(data)
        digest = hashlib.sha256(data).hexdigest()
        with pathlib.Path(str(path) + ".sha256").open("x", encoding="ascii", newline="\n") as f:
            f.write(f"{digest}  {path.name}\n")
        results.append({"name": path.name, "bytes": len(data), "sha256": digest})
    print(json.dumps(results, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--publish", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--epoch", type=int, required=True)
    args = parser.parse_args()
    build(args.publish, args.output, args.version, args.epoch)
