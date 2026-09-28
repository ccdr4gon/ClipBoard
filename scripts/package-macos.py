"""Build a self-contained macOS .app and ZIP (including Unix executable permissions).

Works on Windows and macOS. Code signing is performed on macOS only.
"""
import argparse
import os
from pathlib import Path
import plistlib
import shutil
import stat
import subprocess
import sys
import zipfile

ROOT = Path(__file__).resolve().parent.parent


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--arch", choices=["arm64", "x64"], default="arm64")
    parser.add_argument("--version", default="1.1.0")
    parser.add_argument("--sign", default="-", help="On macOS: Developer ID identity or '-' for local ad-hoc signing")
    args = parser.parse_args()
    output = ROOT / "dist" / f"macos-{args.arch}"
    bundle = output / "ClipBoard.app"
    # This script owns only this generated app bundle, never the user's data or source.
    if bundle.exists():
        if not bundle.resolve().is_relative_to((ROOT / "dist").resolve()):
            raise RuntimeError("Unexpected output path")
        shutil.rmtree(bundle)
    binary = bundle / "Contents" / "MacOS"
    binary.mkdir(parents=True)
    subprocess.run(["dotnet", "publish", str(ROOT / "src/ClipBoard.Mac/ClipBoard.Mac.csproj"),
                    "-c", "Release", "-r", f"osx-{args.arch}", "--self-contained", "true",
                    "-p:UseAppHost=true", "-p:PublishTrimmed=false", "-p:PublishSingleFile=true",
                    "-p:DebugType=None", "-p:DebugSymbols=false",
                    "-p:IncludeNativeLibrariesForSelfExtract=false", "-o", str(binary)], check=True)
    info = {
        "CFBundleName": "ClipBoard", "CFBundleDisplayName": "ClipBoard",
        "CFBundleExecutable": "ClipBoard.Mac", "CFBundleIdentifier": "io.github.ccdr4gon.clipboard",
        "CFBundlePackageType": "APPL", "CFBundleInfoDictionaryVersion": "6.0",
        "CFBundleShortVersionString": args.version, "CFBundleVersion": args.version,
        "LSMinimumSystemVersion": "13.0", "LSUIElement": True, "NSHighResolutionCapable": True,
        "NSPrincipalClass": "NSApplication",
    }
    with (bundle / "Contents/Info.plist").open("wb") as f:
        plistlib.dump(info, f)
    for file in binary.rglob("*"):
        if file.is_file():
            file.chmod(0o755 if file.name == "ClipBoard.Mac" or file.suffix == ".dylib" else 0o644)
    if sys.platform == "darwin":
        for file in binary.rglob("*"):
            # Sign nested native libraries first. Signing the app host can implicitly sign its bundle.
            # Managed assemblies are embedded in the host, avoiding unsigned PE files in Contents/MacOS.
            if file.is_file() and file.suffix == ".dylib":
                sign(file, args.sign)
        sign(bundle, args.sign)
        subprocess.run(["codesign", "--verify", "--deep", "--strict", str(bundle)], check=True)
    else:
        print("Built on Windows/Linux: bundle has not been code-signed or notarized on macOS.")
    archive = output / f"ClipBoard-{args.version}-macos-{args.arch}.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for file in sorted(bundle.rglob("*")):
            relative = file.relative_to(output).as_posix()
            entry = zipfile.ZipInfo(relative + ("/" if file.is_dir() else ""))
            entry.create_system = 3
            mode = 0o755 if file.is_dir() or file.name == "ClipBoard.Mac" or file.suffix == ".dylib" else 0o644
            entry.external_attr = ((stat.S_IFDIR if file.is_dir() else stat.S_IFREG) | mode) << 16
            entry.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(entry, b"" if file.is_dir() else file.read_bytes())
    print(archive)


def sign(path, identity):
    command = ["codesign", "--force", "--sign", identity,
               "--entitlements", str(ROOT / "src/ClipBoard.Mac/macos.entitlements")]
    if identity != "-":
        command += ["--options", "runtime", "--timestamp"]
    subprocess.run(command + [str(path)], check=True)


if __name__ == "__main__":
    main()
