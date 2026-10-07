import importlib.util
import io
import pathlib
import tarfile
import tempfile
import unittest

root = pathlib.Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("updater", root / "linux/packaging/update-linux.py")
updater = importlib.util.module_from_spec(spec)
spec.loader.exec_module(updater)


class Updates(unittest.TestCase):
    def entry(self, version="1.10.0"):
        base = "https://github.com/crmaris/pCUE/releases/download/v" + version + "/"
        return {"apps": {"pcue-linux": {"version": version, "url": base + "test.deb", "sha256": "a" * 64, "portableUrl": base + "test.tar.gz", "portableSha256": "b" * 64}}}

    def test_numeric_versions(self):
        self.assertIsNotNone(updater.release_entry(self.entry(), "1.9.9"))
        self.assertIsNone(updater.release_entry(self.entry("1.7.4"), "1.7.4"))
        self.assertIsNone(updater.release_entry(self.entry("1.7.3"), "1.7.4"))

    def test_bad_version(self):
        for value in ["1.7", "1.7.4x", "../../evil", None]:
            with self.assertRaises(ValueError):
                updater.version(value)

    def test_urls_and_hashes(self):
        for url in ["http://github.com/crmaris/pCUE/releases/download/v1.10.0/a.deb", "https://evil.example/a", "https://github.com/other/repo/releases/download/a"]:
            document = self.entry()
            document["apps"]["pcue-linux"]["url"] = url
            with self.assertRaises(ValueError):
                updater.release_entry(document, "1.9.9")
        document = self.entry()
        document["apps"]["pcue-linux"]["sha256"] = "invalid"
        with self.assertRaises(ValueError):
            updater.release_entry(document, "1.9.9")

    def test_archive_traversal_and_links(self):
        for name, link in [("pcue-linux-1.10.0/../../escape", False), ("/absolute", False), ("pcue-linux-1.10.0//absolute", False), ("pcue-linux-1.10.0/link", True)]:
            with tempfile.TemporaryDirectory(dir=root / ".codex-tmp/linux") as directory:
                folder = pathlib.Path(directory)
                archive = folder / "bad.tar.gz"
                with tarfile.open(archive, "w:gz") as tar:
                    item = tarfile.TarInfo(name)
                    if link:
                        item.type = tarfile.SYMTYPE
                        item.linkname = "/etc/passwd"
                    else:
                        item.size = 1
                    tar.addfile(item, None if link else io.BytesIO(b"x"))
                with self.assertRaises(ValueError):
                    updater.extract_portable(archive, folder / "output", "1.10.0")
                self.assertFalse((folder / "output").exists())


if __name__ == "__main__":
    unittest.main()
