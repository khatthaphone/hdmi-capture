#!/usr/bin/env python3
"""Writes nuget-sources.json for the offline Flatpak build (task 122), like flatpak-builder-tools' dotnet generator.

Restores the Linux app for linux-x64 (self-contained, ReadyToRun: runtime and crossgen packs included) into an empty
package folder, then lists every .nupkg with its nuget.org URL and SHA-512, for flatpak-builder to download into
./nuget-sources. Usage: flatpak-nuget-sources.py <out.json>   (run in WSL/Linux with the .NET 10 SDK).
"""
import base64
import json
import os
import subprocess
import sys
import tempfile

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
PROJECT = os.path.join(REPO, "Apps/Linux/src/HDMICaptain.Linux.App/HDMICaptain.Linux.App.csproj")


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    out = sys.argv[1]
    with tempfile.TemporaryDirectory(prefix="hc-nuget-") as packages, tempfile.TemporaryDirectory(prefix="hc-obj-") as artifacts:
        subprocess.run(["dotnet", "restore", PROJECT, "-r", "linux-x64", "-p:SelfContained=true", "-p:PublishReadyToRun=true",
                        "--packages", packages, "--artifacts-path", artifacts], check=True, stdout=subprocess.DEVNULL)
        sources = []
        for name in sorted(os.listdir(packages)):
            for version in sorted(os.listdir(os.path.join(packages, name))):
                folder = os.path.join(packages, name, version)
                sha_file = os.path.join(folder, f"{name}.{version}.nupkg.sha512")
                if not os.path.exists(sha_file):
                    continue
                sha512 = base64.b64decode(open(sha_file).read().strip()).hex()
                filename = f"{name}.{version}.nupkg"
                sources.append({
                    "type": "file",
                    "url": f"https://api.nuget.org/v3-flatcontainer/{name}/{version}/{filename}",
                    "sha512": sha512,
                    "dest": "nuget-sources",
                    "dest-filename": filename,
                })
    with open(out, "w") as f:
        json.dump(sources, f, indent=2)
        f.write("\n")
    print(f"{len(sources)} packages → {out}")


if __name__ == "__main__":
    main()
