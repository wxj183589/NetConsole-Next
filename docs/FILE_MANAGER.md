# File manager

files exposes only these logical roots: raw, terminal-recordings, inbox, archive, backup, published, analysis, exports and logs. The browser receives root IDs and relative paths, never the absolute data root.

Every request rejects absolute paths, drive-qualified paths, dot-dot segments, control characters and reparse-point traversal. SQLite catalog files, local API tokens, credentials, password material, .open files and recovery markers are blocked. The implementation does not expose delete, move or arbitrary host-shell operations.

Single files are streamed with range support. A file/folder/mixed selection is expanded server-side into an owner-bound background ZIP job. Job state survives a browser refresh while the service process remains alive; it includes file/byte progress, cancellation and 24-hour temp cleanup. ZIP64 is provided by .NET ZipArchive when the archive requires it.
