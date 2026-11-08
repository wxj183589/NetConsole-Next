# R7.5 FIT-AP Report

Status: **BLOCKED** for native login; AC-managed observation remains available.

FIT-AP is `192.168.56.4:23` and remains under its AC Domain. The last credential-free TCP/Telnet probe reached a password prompt. No credential was sent. AC1 previously accepted the authorized running-configuration command:

```text
wlan ap-execute all exec-console enable
```

That response alone does not prove the setting became effective. The available running-config/help inspection did not expose a conclusive readback, and no FIT-AP Telnet authentication was completed. No `save` was issued and no command was repeated in this closure.

Next evidence needed: an approved existing FIT-AP credential and a direct read-only login/version/identity probe. Until then, version, MAC, serial, interface, running state and native Raw/Facts remain unverified. Do not create a separate FIT wireless domain.
