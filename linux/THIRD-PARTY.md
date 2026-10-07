# Linux package dependencies

The Linux package bundles .NET 10 (MIT), Avalonia 11.3.22 (MIT), its Fluent theme
(MIT), SkiaSharp and its native Skia assets (MIT/BSD), HarfBuzzSharp (MIT), and
HidSharp **2.1.0** (Apache-2.0), plus Tmds.DBus.Protocol (MIT). The exact transitive versions and dependency hashes
are recorded in `linux/packages.lock.json`. License and notice files are bundled
under `licenses/`, including the .NET runtime's third-party notices. Upstream:

- https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
- https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT
- https://github.com/AvaloniaUI/Avalonia/blob/master/licence.md
- https://github.com/mono/SkiaSharp/blob/main/LICENSE.md
- https://www.zer7.com/files/oss/hidsharp/LICENSE.txt

The shared cooling driver remains independently implemented, MIT licensed, and
compiled directly from the same canonical files as the Windows application. Its
MIT license is included as `LICENSE`. No third-party fan-control driver is bundled.
