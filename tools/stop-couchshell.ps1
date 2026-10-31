# Stops CouchShell and its background programs. Tachyon is crash-safe, so stopping it this way
# loses nothing it has acknowledged.
foreach ($name in 'CouchShell', 'couchd', 'tachyon') {
    Get-Process $name -ErrorAction SilentlyContinue | Stop-Process -Force
}
