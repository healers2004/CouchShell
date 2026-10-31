# couchd: CouchShell's helper. It does the Windows work (launching, watching apps, volume, power)
# and talks to Tachyon over the network, like any other client.

# The logic, with no OS calls, so it's tested on Linux and Windows.
add_library(couchd_core
  src/agent/agent.cpp
  src/protocol/reply_parser.cpp)
target_include_directories(couchd_core PUBLIC src)

add_executable(couchd_tests
  tests/agent_test.cpp
  tests/reply_parser_test.cpp)
target_link_libraries(couchd_tests PRIVATE couchd_core GTest::gtest_main)
include(GoogleTest)
gtest_discover_tests(couchd_tests)

# The program itself only makes sense on Windows.
if(WIN32)
  add_executable(couchd
    src/platform/win32/main.cpp
    src/platform/win32/windows_platform.cpp)
  target_link_libraries(couchd PRIVATE couchd_core ws2_32 ole32 powrprof shell32 advapi32)
  target_compile_definitions(couchd PRIVATE UNICODE _UNICODE)
endif()
