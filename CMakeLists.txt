cmake_minimum_required(VERSION 3.20)
project(CouchShell LANGUAGES CXX)

# Plain C++20 everywhere (-std=c++20, /std:c++20); fail if the compiler can't do it.
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# Must be set here too: ctest only finds tests when the top-level project enables testing.
enable_testing()

# The backend: an unmodified copy of Tachyon. Never edit it here; update it with `git subtree pull`.
# It also brings in GoogleTest.
add_subdirectory(vendor/tachyon)

add_subdirectory(couchd)
