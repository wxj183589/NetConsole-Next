# Desktop Helper

The Desktop Helper is a small Windows executable launched in the logged-in host desktop session. It is not required for the Web UI to load and it is not a browser-local helper.

The Service connects only to the local named pipe NetConsoleNext.DesktopHelper. The request is limited to OPEN_EXTERNAL_TERMINAL and OPEN_DIRECTORY. The helper allow-lists SecureCRT, Xshell and PuTTY, rejects reparse-point paths, rejects shell metacharacters and starts processes with UseShellExecute=false and ArgumentList.

The Service validates the logged-in Web role, registered tool, enabled Next device profile and device argument schema before sending a request. The helper repeats path and operation validation. Credentials and passwords are never placed in process arguments or audit records. If no interactive host session is listening, the API returns HOST_DESKTOP_UNAVAILABLE.

This PR has not run real SecureCRT, Xshell, PuTTY or Explorer on a Windows desktop. CI build and protocol/role tests do not count as that acceptance.
