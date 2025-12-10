<div align="center">
  <img src="https://cdn-icons-png.flaticon.com/512/10823/10823354.png" width="100" />
  <h1>ISPLedger Enterprise</h1>
  <p><strong>Professional Offline ISP Management Software</strong></p>
</div>

---

## 🚀 Overview

**ISPLedger Enterprise** is a complete, offline-first desktop application designed for Internet Service Providers (ISPs) to manage clients, billing, inventory, and network infrastructure. Built with modern web technologies (React, TypeScript) and packaged with Electron, it offers a fast, secure, and robust solution for business management.

### Key Features

*   **Client Management**: Comprehensive master sheet with search, filter, and bulk actions.
*   **Automated Billing**: Generates monthly bills, tracks dues, and handles partial payments.
*   **Inventory System**: Manage stock (Routers, ONUs, Cables), track asset assignments, and view logs.
*   **Network Diagram**: Interactive topology builder to map your network nodes and clients.
*   **Financial Reports**: Real-time Profit & Loss statements, daily collection reports, and exportable PDFs.
*   **Secure Backup**: 
    *   **Local**: Auto-backups to your device daily (rolling 10 days).
    *   **Cloud**: Optional Google Drive synchronization for off-site data safety.
*   **Network Tools**: Integrated speed tests and system command reference.
*   **Auto-Update**: Automatically checks for and installs new versions from GitHub.

---

## 🛠️ Installation

### Prerequisites
*   Windows 10/11 (64-bit recommended)
*   Internet connection (only for initial installation and updates)

### Steps
1.  Download the latest installer (`.exe`) from the [Releases Page](https://github.com/sobujsehk/ISPLedger-releaseandupdate/releases).
2.  Run the installer. Windows SmartScreen may prompt you; click "Run Anyway" (as this is a private enterprise app).
3.  The application will launch automatically.
4.  **Login**: 
    *   **Default Password**: `admin`
    *   *Important: Change this immediately in Settings > Security.*

---

## 💻 Developer & Build Guide

If you want to build the application from source or contribute.

### 1. Environment Setup
Ensure you have **Node.js** (v18+) and **Git** installed.

```bash
# Clone the repository
git clone https://github.com/YourUsername/ispledger-desktop.git
cd ispledger-desktop

# Install dependencies
npm install
```

### 2. Configuration
The application is pre-configured for production. However, if you are forking the project:

*   **Google Drive API**:
    *   Update `src/constants.ts` with your own `GOOGLE_CLIENT_ID` and `GOOGLE_API_KEY`.
    *   Ensure your Google Cloud Project has the "Google Drive API" enabled.
    *   Configure "OAuth Consent Screen" and add your test users if in testing mode.

*   **Auto-Update**:
    *   Update `package.json` -> `build` -> `publish` with your GitHub repository details (`owner` and `repo`).
    *   You must generate a `GH_TOKEN` (GitHub Personal Access Token) with `repo` scope to publish releases.

### 3. Running Locally (Development)
Run the React app and Electron wrapper concurrently with hot-reload.

```bash
npm run electron:dev
```

### 4. Building for Production
To create the Windows Installer (`.exe`):

```bash
npm run electron:build
```
The output file will be in the `dist/` folder (e.g., `ISPLedger Enterprise Setup 1.0.0.exe`).
### 3. Running Locally (Development)
Run the React app and host wrapper concurrently with hot-reload.

```bash
# Build frontend (Vite)
npm run build

# Then run the WPF host in debug (or run via VS)
dotnet run --project WPF_APP
```

### 4. Building for Production (Packaging)
This project uses a WPF (.NET) host with WebView2 and an Inno Setup installer. Packaging produces a Windows installer (`.exe`) that bundles the host and the `wwwroot` web assets.

Steps:

1. Build frontend assets (Vite) into the `WPF_APP/wwwroot` folder. The repository already expects built files to be present under `WPF_APP/wwwroot`.

```powershell
# from repo root
npm run build
# (copy Vite output into WPF_APP/wwwroot if your build writes to a different folder)
```

2. Publish the WPF host (preferred) so the installer includes the correct runtime files. Use the target runtime that matches your distribution (e.g., `win-x64`).

```powershell
# Publish framework-dependent (smaller) output
dotnet publish WPF_APP -c Release -r win-x64 -p:PublishSingleFile=false

# OR for self-contained single-file (larger, includes runtime)
dotnet publish WPF_APP -c Release -r win-x64 -p:PublishSingleFile=true -p:PublishTrimmed=false --self-contained true
```

3. Build the installer with Inno Setup (Inno Setup must be installed on your machine). The included `installer.iss` expects the published output under `WPF_APP\bin\Release\net8.0-windows\publish` (or will fall back to the build output directory).

Open the `installer.iss` in Inno Setup Compiler and press Compile, or from PowerShell (if `iscc` is on PATH):

```powershell
# Compile installer (requires Inno Setup's ISCC.exe on PATH)
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer.iss
```

4. Output: The installer will be written to the `dist\` folder as configured in `installer.iss` (e.g., `dist\ISPLedger_Setup.exe`).

Notes & Checklist
- Ensure `WPF_APP\wwwroot` contains the production build (HTML/CSS/JS) from the Vite build.
- Ensure the published host output contains `ISPLedger.exe` and its dependent DLLs — the `installer.iss` includes both `publish` and the bin folder as fallback.
- Replace the `SetupIconFile` (`WPF_APP\app.ico`) with your final application icon if needed.

---

## ☁️ Google Drive Sync Setup

To enable Cloud Backup:
1.  Go to **Settings > Backup & Cloud Sync**.
2.  Click **Connect Account**.
3.  A browser window will open asking for Google permissions.
4.  Allow access to "See, create, and delete its own configuration data" (AppData folder).
5.  Once connected, the status will change to "Linked".
6.  You can now use **Upload Backup** and **Restore From Cloud**.

*Note: Data is stored in a hidden AppData folder on your Google Drive, ensuring it doesn't clutter your main drive files.*

---

## 🔄 Auto-Update Mechanism

The application is configured to check for updates from the official GitHub repository.
1.  On app launch, it checks for a newer version.
2.  If found, it downloads in the background (visible in **Settings > Software Updates**).
3.  Once downloaded, a "Restart & Install" button will appear.

## 📦 Updating `latest.json` (Release checksum)

The auto-update checker reads `latest.json` from the repository (see `WPF_APP/services/UpdateService.cs`). To safely publish a new release and let the app verify downloads, provide a SHA256 checksum for the installer in `latest.json`.

You can generate `latest.json` locally or via CI. This project includes helper scripts and a GitHub Actions workflow.

Local (PowerShell):

```powershell
# Compute checksum from a local installer and write latest.json
.\scripts\generate-latest-json.ps1 -Version "1.2.3.0" -InstallerPath ".\dist\ISPLedger-setup-1.2.3.exe" -DownloadUrl "https://github.com/your/repo/releases/download/v1.2.3/ISPLedger-setup-1.2.3.exe" -ReleaseNotes "Fixed bugs and improvements" -Output latest.json
```

Local (bash):

```bash
./scripts/generate-latest-json.sh -v 1.2.3.0 -u "https://github.com/your/repo/releases/download/v1.2.3/ISPLedger-setup-1.2.3.exe" -f ./dist/ISPLedger-setup-1.2.3.exe -n "Release notes here" -o latest.json
```

GitHub Actions (example):

This repo contains a GitHub Actions workflow at `.github/workflows/generate-latest-json.yml` which can be run manually (`workflow_dispatch`). Provide the `download_url` and `version` inputs; the workflow downloads the installer, computes the SHA256, writes `latest.json`, and commits it back to the `main` branch.

Notes:
- The workflow commits using the `GITHUB_TOKEN` and targets the `main` branch by default. Ensure you have permissions/branch protection configured appropriately.
- `latest.json` must be accessible via the `raw.githubusercontent.com` URL used by `UpdateService.VersionUrl`.

---

## 📄 License & Credits

**Developer**: Sabuj Sheikh  
**License**: Enterprise Single User License  
**Stack**: Electron, React, TypeScript, Tailwind CSS, Vite, Recharts, jsPDF.

Copyright © 2025. All Rights Reserved.
