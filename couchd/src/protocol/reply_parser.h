// The client side of RESP2: builds commands and parses the server's replies. TCP can deliver a
// reply in any number of pieces, so bytes are buffered until a whole reply has arrived.
#pragma once

#include <map>
#include <string>
#include <string_view>
#include <vector>

namespace couchd {

struct Reply {
    enum class Kind { kSimple, kError, kInteger, kBulk, kNull, kArray };

    Kind kind = Kind::kNull;
    std::string text;          // kSimple, kError, kBulk
    long long integer = 0;     // kInteger
    std::vector<Reply> items;  // kArray

    // HGETALL replies with field, value, field, value...
    std::map<std::string, std::string> to_map() const;
};

// "GET foo" as the bytes *2\r\n$3\r\nGET\r\n$3\r\nfoo\r\n
std::string encode_command(const std::vector<std::string>& args);

enum class ParseStatus {
    kNeedMore,  // only part of a reply has arrived
    kReply,     // a whole reply was stored in the output argument
    kError,     // malformed; the connection can't be trusted any more
};

class ReplyParser {
public:
    void feed(std::string_view data);
    ParseStatus next(Reply& reply);

private:
    std::string buffer_;
};

}  // namespace couchd
