| <h1>LibreSpotUWP</h1> | <img src="https://github.com/user-attachments/assets/b53f3efe-a089-4e68-8caa-57a0bf2693a9" width="60" height="60"> |
| --- | --- |

**LibreSpotUWP** is a Spotify client designed with UWP in mind, powered by LibreSpot. It supports Spotify Premium-based accounts, and works with Spotfiy Connect.

**Disclaimer:** This is an unofficial, third-party implementation of a Spotify client for the Universal Windows Platform (UWP). This project is not affiliated with, endorsed, or sponsored by Spotify AB. Spotify is a trademark of Spotify AB.

| Lumia 950 XL | Surface Duo | PC |
|--------------|-------------|-------------|
| <img width="250" height="440" alt="Lumia 950 XL - Windows 10 Mobile - Now Playing Page" src="https://github.com/user-attachments/assets/9c04b10f-d6fb-497e-b5a1-9a6a4d5a62a0" /> <img width="250" height="440" alt="Lumia 950 XL - Windows 10 Mobile - Lyrics Page using Spotify Colour Theme" src="https://github.com/user-attachments/assets/b84d7866-a252-4612-b0e6-e0301d8d6b71" /> | TODO: Add photos | <img width="666" height="444" alt="Windows 11 - Liked Songs Page" src="https://github.com/user-attachments/assets/f74e715f-85ab-4d7e-acb4-d21a02980b5b" /> <img width="666" height="444" alt="Windows 11 - Lyrics Page using Spotify Colour Theme" src="https://github.com/user-attachments/assets/6ab08ecf-9a84-40df-9df1-4493f3552104" /> |
| _Windows 10 Mobile – 15254.603, ARM_ | _Andromeda OS (8828080) – 18236.1000, ARM64_ | _Windows 11 - 26200.8655, X64_ |

## Features

*   **Fully Featured:** Has support for synced lyrics, SMTC (System Media Transport Control - For track metadata across apps and accessories) and more.
*   **Offline Compatible:** Designed to work offline and online (or anything in-between), along with support for persisting tracks (for upto 30 days at a time).
*   **Native & Lightweight:** A fully functional Spotify client running as a UWP app - Uses upto **67%** less RAM than the official Spotify client!
*   **Universal:** Runs natively on **x64**, **x86**, **ARM64**, and **ARM32**.
*   **Fluent:** Features a fluent Windows design, matching the OS it is installed on (Windows 10, Windows 10 Fluent (Acrylic) and Windows 11 Mica (AKA Sun Valley).

## Download
### See the [Latest Release](https://github.com/megabytesme/LibreSpotUWP/releases/latest).

## Build Guide

Building LibreSpotUWP is unique because it combines a C# UWP Host with a Rust Dynamic Library (`librespot.dll`).

### Prerequisites

*   **Rust Nightly Toolchain:** Required for the `-Z build-std` flag.
*   **Visual Studio 2017 (or newer - 2026 recommended):**
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
