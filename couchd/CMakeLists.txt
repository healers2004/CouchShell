# Everything except main(), so tests can link it without starting the server.
add_library(couchd_core
  src/commands/commands.cpp
  src/commands/store.cpp
  src/protocol/resp_parser.cpp
  src/protocol/resp_writer.cpp
  src/server/server.cpp)
# Lets code write #include "protocol/resp_parser.h".
target_include_directories(couchd_core PUBLIC src)

# One socket implementation per OS, behind the same platform/socket.h.
if(WIN32)
  target_sources(couchd_core PRIVATE src/platform/win32/socket.cpp)
  target_link_libraries(couchd_core PUBLIC ws2_32)  # the Winsock library
else()
  target_sources(couchd_core PRIVATE src/platform/posix/socket.cpp)
endif()

# The server executable. Doesn't link Cushion yet; storage arrives in Phase 3.
add_executable(couchd src/main.cpp)
target_link_libraries(couchd PRIVATE couchd_core)

add_executable(couchd_tests
  tests/commands_test.cpp
  tests/resp_parser_test.cpp
  tests/resp_writer_test.cpp
  tests/store_test.cpp)
target_link_libraries(couchd_tests PRIVATE couchd_core GTest::gtest_main)
gtest_discover_tests(couchd_tests)
