# Static library: an archive of compiled .cpp files that programs link against.
add_library(cushion src/version.cpp)
# PUBLIC: Cushion and everything that links it can #include "cushion/...".
target_include_directories(cushion PUBLIC include)

# Test program: our tests + Cushion + GoogleTest's ready-made main().
add_executable(cushion_tests tests/version_test.cpp)
target_link_libraries(cushion_tests PRIVATE cushion GTest::gtest_main)
# Registers each TEST(...) with ctest.
gtest_discover_tests(cushion_tests)
