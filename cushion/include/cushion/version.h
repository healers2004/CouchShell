// Placeholder API that proves the library builds and links. Replaced by storage in Phase 3.
#pragma once  // include this header at most once per .cpp (replaces #ifndef guards)

#include <string>

// Everything in Cushion lives in this namespace; callers write cushion::version().
namespace cushion {

// Returns the library version. std::string owns its memory, so the caller never frees it.
std::string version();

}  // namespace cushion
