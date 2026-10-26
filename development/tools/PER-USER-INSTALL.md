# Per-user installer operations

The PowerShell installer writes the package to `%LOCALAPPDATA%\Programs\PDF - Paperwork Done Faster`. It does not require elevation or register PDF associations. Archive installs should use the adjacent `.sha256` file; directory installs are intended for controlled package preparation and disposable checks.

## Install

Run `Install-PerUser.ps1` from the extracted package. The ownership manifest records the numeric package version and SHA-256 digest of every installed package file. The app's user data stays outside the install directory.

For a package stored in OneDrive, prefer the immutable ZIP and its adjacent `.sha256` sidecar rather than copying the synced extracted directory. From inside the extracted v10 folder, with the ZIP beside that folder:

```powershell
.\Install-PerUser.ps1 -PackageArchive ..\portable-win-x64-20261016-internal-v10.zip
```

Add `-Upgrade` for a newer version or `-Repair` for the same version. The archive path preflights its checksum and extracts to a private temporary directory. The v10 disposable directory-source upgrade encountered a transient OneDrive `desktop.ini` copy failure; that path is not accepted as a reliable synced-directory install workflow. The separate archive lifecycle report records its actual result.

## Upgrade

Use a newer package and explicitly opt in:

```powershell
.\Install-PerUser.ps1 -Upgrade -PackageVersion 1.2.1
```

If `-PackageVersion` is omitted, the installer reads the executable's ProductVersion or FileVersion. Versions must be numeric (`major.minor.patch` with an optional fourth component). Upgrade requires a version newer than the installed version.

Before changing files, the installer checks the schema-2 manifest, every tracked file digest, and the install tree. Modified tracked files, reparse points, unexpected files, and unrecognized or legacy installs stop the operation without replacing the install. It prepares the new package beside the install and swaps the folders, rolling the previous folder back if the swap fails.

## Repair

Use the same package version to restore missing package files:

```powershell
.\Install-PerUser.ps1 -Repair -PackageVersion 1.2.1
```

Repair requires the package's file inventory and digests to match the installed manifest. It restores missing files only. It refuses modified files and does not overwrite user additions.

Installs created by older schema-1 installers are not migrated automatically. Keep a copy of any user-added files and use the matching old uninstall flow or choose a separate install folder before using this installer. Uninstall removes only manifest-owned files, matching the installed app shortcut, and empty package directories; it retains user data outside the install folder.

## Disposable verification

Run `Test-PerUserInstall.ps1` and `Test-PerUserUninstall.ps1` to exercise package checksum and archive checks, fresh install, repair, upgrade, conservative refusal, and uninstall preservation in temporary directories. These checks do not establish physical-device, printer, clean-machine, or installed-shell acceptance.

From the development checkout, `tools/Test-PackagedLifecycle.ps1 -PackageDirectory <new-package> -PreviousPackageDirectory <older-package> -OutputDirectory <fresh-evidence-folder>` tests the shipped older install, real package upgrade and missing-file repair, exact installed inventory, shortcut target, installed runtime smoke, hidden desktop launch and uninstall ownership preservation in one disposable location. `tools/verify-package-integrity.py` separately checks ZIP/payload byte identity and retains source/notice inventories. These are host checks; clean-machine and physical acceptance remain separate.
