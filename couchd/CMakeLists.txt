# Everything except main(), so tests can link it without starting the server.
add_library(couchd_core src/protocol/resp_parser.cpp)
# Lets code write #include "protocol/resp_parser.h".
target_include_directories(couchd_core PUBLIC src)

# The server executable. Doesn't link Cushion yet; storage arrives in Phase 3.
add_executable(couchd src/main.cpp)
target_link_libraries(couchd PRIVATE couchd_core)

add_executable(couchd_tests tests/resp_parser_test.cpp)
target_link_libraries(couchd_tests PRIVATE couchd_core GTest::gtest_main)
gtest_discover_tests(couchd_tests)
