# Third-Party Notices

SteamDepotFS is licensed under the Apache License, Version 2.0. See
`LICENSE` and `NOTICE` in this distribution.

Self-contained binary distributions of SteamDepotFS include the .NET runtime
and may include third-party NuGet packages. Those components remain under
their own licenses.

## Runtime Components

| Component | Version | License | Authors | Project |
| --- | --- | --- | --- | --- |
| Microsoft .NET Runtime | 8.0.x | MIT | Microsoft | https://github.com/dotnet/runtime |

## NuGet Packages

| Package | Version | License | Authors | Project |
| --- | --- | --- | --- | --- |
| SteamKit2 | 3.4.0 | LGPL-2.1-only | SteamKit2 | https://github.com/SteamRE/SteamKit |
| Mono.Fuse.NETStandard | 1.1.0 | MIT | Jonathan Pryor, Alexey Kolpakov | https://github.com/alhimik45/Mono.Fuse.NETStandard |
| Mono.Posix.NETStandard | 1.0.0 | Package license URL: https://go.microsoft.com/fwlink/?linkid=869050 | Microsoft | https://go.microsoft.com/fwlink/?linkid=869051 |
| protobuf-net | 3.2.56 | Apache-2.0 | Marc Gravell | https://github.com/protobuf-net/protobuf-net |
| protobuf-net.Core | 3.2.56 | Apache-2.0 | Marc Gravell | https://github.com/protobuf-net/protobuf-net |
| System.IO.Hashing | 10.0.1 | MIT | Microsoft | https://dot.net/ |
| ZstdSharp.Port | 0.8.7 | MIT | Oleg Stepanischev | https://github.com/oleg-st/ZstdSharp |

## LGPL Notice For SteamKit2

SteamDepotFS uses SteamKit2, which is licensed under LGPL-2.1-only. The
SteamKit2 source repository is available at:

https://github.com/SteamRE/SteamKit

The NuGet package used by this project identifies the source commit as:

`1c7bc9c41a529e8fbb1e6890f1e4dbcdc5200cb7`

SteamDepotFS does not modify SteamKit2. If you receive a binary distribution
that includes SteamKit2, you may replace or relink the SteamKit2 library with
a modified compatible version under the terms of the LGPL-2.1-only license.
SteamDepotFS source and build instructions are available in this repository.
