| <h1>LibreSpotUWP</h1> | <img src="https://github.com/user-attachments/assets/b53f3efe-a089-4e68-8caa-57a0bf2693a9" width="60" height="60"> |
| --- | --- |



**LibreSpotUWP** is a Spotify client designed with UWP in mind, powered by LibreSpot. It supports Spotify Premium-based accounts, and works with Spotfiy Connect.

**Disclaimer:** This is an unofficial, third-party implementation of a Spotify client for the Universal Windows Platform (UWP). This project is not affiliated with, endorsed, or sponsored by Spotify AB. Spotify is a trademark of Spotify AB.

## Features

*   **Native & Lightweight:** A fully functional Spotify client running as a UWP app.
*   **Universal:** Runs natively on **x64**, **x86**, **ARM64**, and **ARM32**.
*   **Fluent:** Features a fluent Windows design.

## Download
### See the [Latest Release](https://github.com/megabytesme/LibreSpotUWP/releases/latest).
#### Windows Store releases are upcoming...

## Build Guide

Building LibreSpotUWP is unique because it combines a C# UWP Host with a Rust Dynamic Library (`librespot.dll`).

### Prerequisites

*   **Rust Nightly Toolchain:** Required for the `-Z build-std` flag.
*   **Visual Studio 2017 (or newer):**
    *   **Workload:** Universal Windows Platform development.
    *   **Workload:** Desktop development with C++.
*   **Windows SDKs:** You **must** install these specific SDK versions via the VS Installer:
    *   **10.0.10240.0** (Required for x86, x64, and ARM32 compatibility).
    *   **10.0.16299.0** (Required for ARM64 compatibility).
 
## **1. Building the librespot Core (Rust)**  
librespot is written in Rust and compiled as a **UWP‑safe DLL** (`librespot.dll`).  

1) **Clone this librespot repository fork: https://github.com/megabytesme/librespot**

    `git clone https://github.com/megabytesme/librespot.git`

2) **Run the `deps.ps1` script from the root of the cloned repository before building to restore the required build dependancies!**

    `.\deps.ps1`
   
3) **Run the appropriate build command from the root of the cloned repository:**

| Target | Command | Typical Devices |
|--------|--------|-------|
| **ARM32** | `cargo +nightly build -Z "build-std=std,panic_abort" --target thumbv7a-uwp-windows-msvc --release --no-default-features --features "native-tls uwp-backend"` | Windows 10 Mobile, Windows RT Devices |
| **ARM64** | `cargo +nightly build -Z "build-std=std,panic_abort" --target aarch64-uwp-windows-msvc --release --no-default-features --features "native-tls uwp-backend"` | Copilot Plus PCs, HoloLens 2, Surface Duo, WoA, Snapdragon or Microsoft SQ1-SQ3 |
| **x86** | `cargo +nightly build -Z "build-std=std,panic_abort" --target i686-uwp-windows-msvc --release --no-default-features --features "native-tls uwp-backend"` | Older PCs (typically with 4GB RAM or less), emulators, HoloLens 1 |
| **x64** | `cargo +nightly build -Z "build-std=std,panic_abort" --target x86_64-uwp-windows-msvc --release --no-default-features --features "native-tls uwp-backend"` | Modern PCs |

Each script outputs a DLL to:

```
target/(ARCHITECTURE - i.e thumbv7a, aarch64, i686 or x86_64)-uwp-windows-msvc/release/librespot.dll
```

## **2. Building the UWP Host App (C#)**  
Open the solution:

```
LibreSpotUWP.sln
```

### **Steps:**
1. Copy the generated `librespot.dll` into the **root directory** of the UWP project you intend to run.  
2. In Visual Studio, set **Solution Platform** to match your target device:  
   - `ARM` → Windows 10 Mobile (MUST BE RELEASE BUILD)  
   - `ARM64` → HoloLens 2, Windows on ARM  
   - `x86` → Emulators, older PCs  
   - `x64` → Modern PCs  
3. Press **F5** to deploy and run.
