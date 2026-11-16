"""Builds the versioned release zip: installer, BepInEx, plugin and player documentation.
Same zip for Steam, GOG and Epic. Uses forward-slash paths so every unzip tool reads it.

Also builds the Thunderstore update zip for the existing package
GungeonOnline-GungeonOnlineCoOp. The manifest name is immutable on Thunderstore
and must stay GungeonOnlineCoOp.
"""
import os
import argparse
import json
import re
import struct
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
with open(os.path.join(ROOT, "src", "GungeonOnline", "Plugin.cs"), encoding="utf-8") as source:
    VERSION = re.search(r'const string Version = "([^"]+)"', source.read()).group(1)
OUT = os.path.join(ROOT, "dist", "GungeonOnline-" + VERSION + ".zip")
# Team GungeonOnline, package GungeonOnlineCoOp. A different name would be a new package, not an update.
TS_NAME = "GungeonOnlineCoOp"
TS_DESCRIPTION = (
    "Online co-op for Enter the Gungeon (beta). Own camera, any character, up to 8 players "
    "(more than 2 is experimental). Steam, GOG and Epic cross-play. Free and open source. "
    "Not affiliated with Dodge Roll or Devolver."
)
TS_DEPENDENCIES = ["BepInEx-BepInExPack_EtG-5.4.2101"]
TS_OUT = os.path.join(ROOT, "dist", TS_NAME + "-" + VERSION + "-thunderstore.zip")
NAME_RE = re.compile(r"^[a-zA-Z0-9_]+$")
VERSION_RE = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+$")
COMPONENT_RE = re.compile(r"^[a-zA-Z0-9]+([a-zA-Z0-9_]+[a-zA-Z0-9])?$")


def add_tree(z, src, prefix):
    for base, _, files in os.walk(src):
        for f in files:
            if f == "changelog.txt":
                continue
            full = os.path.join(base, f)
            rel = os.path.relpath(full, src).replace(os.sep, "/")
            z.write(full, prefix + rel)


def canonical_version(value):
    return ".".join(str(int(part)) for part in value.split("."))


def parse_dependency(value):
    # Thunderstore parses {namespace}-{name}-{version} from the right. Name has no dashes.
    version = value.split("-")[-1]
    if version.count(".") != 2 or value.count("-") < 2:
        raise ValueError("Invalid dependency reference: " + value)
    if canonical_version(version) != version or not VERSION_RE.match(version):
        raise ValueError("Dependency version must be Major.Minor.Patch without leading zeros: " + value)
    rest = value[: -(len(version) + 1)]
    name = rest.split("-")[-1]
    namespace = "-".join(rest.split("-")[:-1])
    if not namespace or not name:
        raise ValueError("Invalid dependency reference: " + value)
    for label, component in (("namespace", namespace), ("name", name)):
        if not COMPONENT_RE.match(component):
            raise ValueError("Dependency " + label + " is not a Thunderstore component: " + component)
    return namespace, name, version


def png_size(data):
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("icon.png is not a PNG")
    if data[12:16] != b"IHDR":
        raise ValueError("icon.png is missing IHDR")
    width, height, bit_depth, color_type = struct.unpack(">IIBB", data[16:26])
    if b"acTL" in data:
        raise ValueError("icon.png looks animated; Thunderstore only shows the first frame reliably")
    return width, height, bit_depth, color_type


def validate_thunderstore(path):
    """Checks that match Thunderstore's upload validator (package_upload.py, icon.py, manifest fields)."""
    errors = []
    with zipfile.ZipFile(path) as z:
        if z.testzip():
            errors.append("zip failed its CRC test")
        infos = z.infolist()
        names = [i.filename for i in infos]
        if not any(i.header_offset == 0 for i in infos):
            errors.append("zip has data before the first file (Thunderstore rejects a non-zero start)")
        lowered = []
        for name in names:
            if (
                name.startswith("..") or name.startswith("/") or name.startswith("\\")
                or "/.." in name or "//" in name or "./" in name
                or "\\" in name
            ):
                errors.append("unsafe zip path: " + name)
            if name.lower() in lowered:
                errors.append("duplicate filename ignoring case: " + name)
            lowered.append(name.lower())
        required = ("manifest.json", "icon.png", "README.md", "CHANGELOG.md", "LICENSE", "plugins/GungeonOnline.dll")
        for name in required:
            if name not in names:
                errors.append("missing " + name)
        if "manifest.json" in names:
            raw = z.read("manifest.json")
            if raw.startswith(b"\xef\xbb\xbf"):
                errors.append("manifest.json starts with a UTF-8 BOM")
            try:
                manifest = json.loads(raw.decode("utf-8"))
            except (UnicodeDecodeError, json.JSONDecodeError) as exc:
                errors.append("manifest.json is not UTF-8 JSON: " + str(exc))
                manifest = None
            if isinstance(manifest, dict):
                allowed = {"name", "version_number", "website_url", "description", "dependencies"}
                extra = set(manifest) - allowed
                if extra:
                    errors.append("manifest has fields Thunderstore does not need: " + ", ".join(sorted(extra)))
                if manifest.get("name") != TS_NAME or not NAME_RE.match(manifest.get("name", "")):
                    errors.append("manifest name must be exactly " + TS_NAME)
                version = manifest.get("version_number", "")
                if version != VERSION or not VERSION_RE.match(version) or canonical_version(version) != version:
                    errors.append("version_number must be " + VERSION + " with no leading zeros")
                website = manifest.get("website_url", None)
                if not isinstance(website, str) or not website.startswith("https://"):
                    errors.append("website_url must be an https URL")
                elif len(website) > 1024:
                    errors.append("website_url is longer than 1024 characters")
                description = manifest.get("description", "")
                if not isinstance(description, str) or not description.strip():
                    errors.append("description must be a non-empty string")
                elif len(description) > 250:
                    errors.append("description is " + str(len(description)) + " characters; wiki limit is 250")
                elif len(description) > 256:
                    errors.append("description exceeds the manifest field length")
                deps = manifest.get("dependencies")
                if deps != TS_DEPENDENCIES:
                    errors.append("dependencies must be " + json.dumps(TS_DEPENDENCIES))
                elif isinstance(deps, list):
                    for dep in deps:
                        try:
                            parse_dependency(dep)
                        except ValueError as exc:
                            errors.append(str(exc))
        if "icon.png" in names:
            icon = z.read("icon.png")
            if len(icon) > 6 * 1024 * 1024:
                errors.append("icon.png is larger than 6 MB")
            try:
                width, height, bit_depth, color_type = png_size(icon)
                if (width, height) != (256, 256):
                    errors.append("icon.png is %sx%s, must be 256x256" % (width, height))
                if bit_depth != 8 or color_type not in (2, 3, 6):
                    errors.append("icon.png must be an 8-bit RGB, palette or RGBA PNG")
            except ValueError as exc:
                errors.append(str(exc))
        for markdown_name in ("README.md", "CHANGELOG.md"):
            if markdown_name not in names:
                continue
            data = z.read(markdown_name)
            if data.startswith(b"\xef\xbb\xbf"):
                errors.append(markdown_name + " starts with a UTF-8 BOM")
            try:
                text = data.decode("utf-8")
            except UnicodeDecodeError as exc:
                errors.append(markdown_name + " is not UTF-8: " + str(exc))
                continue
            if len(text) > 100000:
                errors.append(markdown_name + " is longer than Thunderstore's 100000 character markdown limit")
        if "README.md" in names:
            readme = z.read("README.md").decode("utf-8")
            for phrase in ("AI", "Claude", "Anthropic", "Network connections and privacy", "WINEDLLOVERRIDES", "r2modman"):
                if phrase not in readme:
                    errors.append("README.md is missing " + phrase)
        if "plugins/GungeonOnline.dll" in names:
            dll = z.read("plugins/GungeonOnline.dll")
            if not dll.startswith(b"MZ"):
                errors.append("plugins/GungeonOnline.dll is not a Windows executable")
            version_utf16 = ("this is " + VERSION).encode("utf-16le")
            if version_utf16 not in dll and VERSION.encode("utf-16le") not in dll:
                errors.append("plugin DLL does not contain version " + VERSION)
    if errors:
        raise SystemExit("Thunderstore zip is not valid:\n- " + "\n- ".join(errors))
    print("Thunderstore validation passed:", path)


def build_thunderstore(plugin_path):
    if not os.path.isfile(plugin_path):
        raise SystemExit("Missing plugin DLL: " + plugin_path)
    os.makedirs(os.path.dirname(TS_OUT), exist_ok=True)
    if os.path.exists(TS_OUT):
        os.remove(TS_OUT)
    if len(TS_DESCRIPTION) > 250:
        raise SystemExit("Thunderstore description is too long")
    manifest = {
        "name": TS_NAME,
        "version_number": VERSION,
        "website_url": "https://github.com/Hero88go/GungeonOnlineCoOpMod",
        "description": TS_DESCRIPTION,
        "dependencies": TS_DEPENDENCIES,
    }
    # Root files, forward slashes, no wrapper folder. plugins/ is where BepInEx pack installs the DLL.
    with zipfile.ZipFile(TS_OUT, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("manifest.json", json.dumps(manifest, indent=4) + "\n")
        z.write(os.path.join(ROOT, "tools", "thunderstore", "icon.png"), "icon.png")
        z.write(os.path.join(ROOT, "tools", "thunderstore", "README.md"), "README.md")
        z.write(os.path.join(ROOT, "CHANGELOG.md"), "CHANGELOG.md")
        z.write(os.path.join(ROOT, "LICENSE"), "LICENSE")
        z.write(plugin_path, "plugins/GungeonOnline.dll")
    validate_thunderstore(TS_OUT)
    print("Package:", TS_OUT)
    return TS_OUT


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bepinex-dir", default=os.path.join(ROOT, "third_party", "bepinex"))
    ap.add_argument("--plugin", default=os.path.join(ROOT, "src", "GungeonOnline", "bin", "Release", "GungeonOnline.dll"))
    ap.add_argument("--thunderstore-only", action="store_true", help="Build only the Thunderstore zip. Does not need the BepInEx payload.")
    args = ap.parse_args()
    build_thunderstore(args.plugin)
    if args.thunderstore_only:
        return
    for required in ("winhttp.dll", os.path.join("BepInEx", "core", "BepInEx.dll")):
        if not os.path.isfile(os.path.join(args.bepinex_dir, required)):
            raise SystemExit("Missing BepInEx payload: " + required)
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    if os.path.exists(OUT):
        os.remove(OUT)
    with zipfile.ZipFile(OUT, "w", zipfile.ZIP_DEFLATED) as z:
        add_tree(z, args.bepinex_dir, "files/")
        z.write(args.plugin, "files/BepInEx/plugins/GungeonOnline/GungeonOnline.dll")
        for f in os.listdir(os.path.join(ROOT, "tools", "installer")):
            z.write(os.path.join(ROOT, "tools", "installer", f), f)
        z.write(os.path.join(ROOT, "docs", "HOW-TO-PLAY.txt"), "HOW-TO-PLAY.txt")
        for name in ("CHANGELOG.md", "LICENSE", "THIRD-PARTY-NOTICES.md"):
            z.write(os.path.join(ROOT, name), name)
    print("Package:", OUT)


if __name__ == "__main__":
    main()
