#!/bin/bash
# What the iPhone's app renews and updates itself with (src/ffi.rs), as a framework for iPhones. On a Mac, with Xcode and Rust:
#   helper/ios.sh OUT      makes OUT/umprenew.framework
# release.yml attaches it to every release as umprenew-ios.zip, which the app's tools/ios-deps.sh downloads, pinned with its hash.
# Not signed: the app is, frameworks included, when it's installed.
set -euo pipefail

mkdir -p "$1"
OUT="$(cd "$1" && pwd)"
cd "$(dirname "$0")"
# As the app's.
export IPHONEOS_DEPLOYMENT_TARGET=15.0
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' ../src/Installer/Installer.csproj)

rustup target add aarch64-apple-ios
cargo rustc --release --locked --lib --features phone --crate-type cdylib --target aarch64-apple-ios -- \
    -C link-arg=-Wl,-install_name,@rpath/umprenew.framework/umprenew

FW="$OUT/umprenew.framework"
rm -rf "${FW:?}"
mkdir "$FW"
cp target/aarch64-apple-ios/release/libumprenew.dylib "$FW/umprenew"
cp App-THIRD-PARTY-NOTICES.txt ../LICENSE ../licenses/LGPL-2.1.txt ../licenses/THIRD-PARTY-NOTICES.txt "$FW/"
cat > "$FW/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>en</string>
    <key>CFBundleExecutable</key>
    <string>umprenew</string>
    <key>CFBundleIdentifier</key>
    <string>com.kirbohq.ultimatemp3player.umprenew</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>umprenew</string>
    <key>CFBundlePackageType</key>
    <string>FMWK</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleVersion</key>
    <string>1</string>
    <key>CFBundleSupportedPlatforms</key>
    <array>
        <string>iPhoneOS</string>
    </array>
    <key>MinimumOSVersion</key>
    <string>$IPHONEOS_DEPLOYMENT_TARGET</string>
</dict>
</plist>
EOF

# The three the app calls have to be there to be found.
for name in ump_run ump_answer ump_cancel; do nm -gU "$FW/umprenew" | grep -q " _$name\$" || { echo "$name isn't exported" >&2; exit 1; }; done
echo "$FW"
