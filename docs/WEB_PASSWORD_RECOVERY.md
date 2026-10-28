# Web password recovery

Password recovery is a local administrator operation, not an anonymous Web endpoint:

    NetConsole.Next.LocalManager.exe web-password reset
    tools/重置Web密码.cmd
    tools/Reset-WebPassword.ps1

The command requires an elevated Windows administrator prompt, asks for admin or superadmin, and then asks whether to restore the documented default or enter a custom password. Passwords are read interactively and are not command-line arguments or log fields. The SQLite catalog is opened with a busy timeout whether the Service is running or stopped.

The operation changes only the PBKDF2 password material, writes a small audit row without the password, and revokes every session for the selected user. Revocation invalidates the corresponding CSRF tokens. It never deletes the database, Raw files, terminal recordings or DPAPI credential vault.
