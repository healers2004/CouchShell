cmake_minimum_required(VERSION 3.20)
project(CouchShell LANGUAGES CXX)

# Must be set here too: ctest only finds tests when the top-level project enables testing.
enable_testing()

# The backend: an unmodified copy of Tachyon. Never edit it here; update it with `git subtree pull`.
add_subdirectory(vendor/tachyon)
