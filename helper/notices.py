# Writes licenses/Helper-THIRD-PARTY-NOTICES.txt: every Rust library inside ump-helper on the three systems the packages are
# built for, with its license and its own notice files; and App-THIRD-PARTY-NOTICES.txt, the same for what the iPhone's app carries
# (ios.sh). Run it from helper/ after Cargo.lock changes:
#   python notices.py
# Build-time-only crates (build scripts, macros) aren't in the program, so they aren't listed.
import io
import json
import os
import subprocess
import sys

HELPER = (os.path.join("..", "licenses", "Helper-THIRD-PARTY-NOTICES.txt"), [],
          ["x86_64-pc-windows-msvc", "x86_64-unknown-linux-gnu", "aarch64-apple-darwin"],
          "ump-helper (the program beside Ultimate MP3 Player Installer that signs and installs)")
APP = ("App-THIRD-PARTY-NOTICES.txt", ["--features", "phone"], ["aarch64-apple-ios"],
       "umprenew (what Ultimate MP3 Player renews itself with on the iPhone)")
# For a library whose package carries no notice file of its own.
NO_FILE = ("Its package has no notice file: the license is the standard text, Apache 2.0 at the start of this file, MIT in\n"
           "THIRD-PARTY-NOTICES.txt, LGPL 2.1 in LGPL-2.1.txt; the copyright holders are its authors.\n\n")


def inside(target, features):
    meta = json.loads(subprocess.run(
        ["cargo", "metadata", "--format-version", "1", "--locked", "--filter-platform", target] + features,
        capture_output=True, check=True).stdout)
    packages = {p["id"]: p for p in meta["packages"]}
    nodes = {n["id"]: n for n in meta["resolve"]["nodes"]}
    seen, todo = set(), [meta["resolve"]["root"]]
    while todo:
        node = nodes[todo.pop()]
        for dep in node["deps"]:
            # Only what's linked in: not build scripts' crates, not macros that run in the compiler.
            if not any(k["kind"] is None for k in dep["dep_kinds"]):
                continue
            p = packages[dep["pkg"]]
            if any("proc-macro" in t["kind"] for t in p["targets"]) or dep["pkg"] in seen:
                continue
            seen.add(dep["pkg"])
            todo.append(dep["pkg"])
    return {i: packages[i] for i in seen}


def notice_files(p):
    folder = os.path.dirname(p["manifest_path"])
    names = sorted(f for f in os.listdir(folder)
                   if f.upper().startswith(("LICENSE", "LICENCE", "COPYING", "NOTICE", "COPYRIGHT", "UNLICENSE")))
    if p.get("license_file"):
        names.append(os.path.relpath(os.path.join(folder, p["license_file"]), folder))
    found = []
    for name in dict.fromkeys(names):
        path = os.path.join(folder, name)
        if os.path.isfile(path):
            found.append((name, io.open(path, encoding="utf-8", errors="replace").read().strip()))
    return found


def is_apache(text):
    return "Apache License" in text and "Version 2.0, January 2004" in text and "END OF TERMS AND CONDITIONS" in text


def write(path, features, targets, what):
    all_packages = {}
    systems = {}
    for t in targets:
        for i, p in inside(t, features).items():
            all_packages[i] = p
            systems.setdefault(i, []).append(t)
    out = io.StringIO()
    out.write(what + " - the Rust libraries\n")
    out.write("inside it, each under its own license. Where a library offers a choice of licenses, it's used under the first\n")
    out.write("one it names. The Apache License 2.0 text is at the start, once, for every library under it; every other notice\n")
    out.write("is each library's own file.\n\n")
    apache_text = None
    entries = []
    for i, p in sorted(all_packages.items(), key=lambda kv: (kv[1]["name"], kv[1]["version"])):
        files = notice_files(p)
        for _, text in files:
            if apache_text is None and is_apache(text):
                apache_text = text
        only = "" if len(systems[i]) == len(targets) else " (only on " + ", ".join(systems[i]) + ")"
        entries.append((p, only, [(n, t) for n, t in files if not is_apache(t)]))
    if apache_text:
        out.write("=" * 100 + "\nApache License 2.0\n" + "=" * 100 + "\n\n" + apache_text + "\n\n")
    for p, only, files in entries:
        out.write("=" * 100 + "\n")
        out.write(f"{p['name']} {p['version']}{only}\n")
        out.write(f"License: {p['license'] or 'see its files'}\n")
        if p.get("repository"):
            out.write(f"Source: {p['repository']}\n")
        if p.get("authors"):
            out.write("Authors: " + ", ".join(p["authors"]) + "\n")
        out.write("=" * 100 + "\n\n")
        for name, text in files:
            out.write(f"--- {name}\n\n{text}\n\n")
        if not files:
            out.write(NO_FILE)
    io.open(path, "w", encoding="utf-8", newline="\n").write(out.getvalue())
    print(f"{len(entries)} libraries -> {path}")


def main():
    write(*HELPER)
    write(*APP)


if __name__ == "__main__":
    sys.exit(main())
