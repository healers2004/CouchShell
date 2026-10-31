#include "protocol/reply_parser.h"

#include <charconv>
#include <cstddef>
#include <system_error>

namespace couchd {

namespace {

// Far more than any reply couchd asks for; stops a bad server using unbounded memory.
const long long kMaxBulkBytes = 16 * 1024 * 1024;
const long long kMaxArrayItems = 1024 * 1024;
const int kMaxDepth = 8;

// Parses the reply at buf[pos]. On kReply, moves pos past it. Nothing is consumed until a whole
// reply is present, because the caller retries from the start after more bytes arrive.
ParseStatus parse_value(std::string_view buf, std::size_t& pos, Reply& reply, int depth) {
    if (depth > kMaxDepth) return ParseStatus::kError;

    std::size_t end = buf.find("\r\n", pos);
    if (end == std::string_view::npos) return ParseStatus::kNeedMore;
    if (end == pos) return ParseStatus::kError;

    char type = buf[pos];
    std::string_view line = buf.substr(pos + 1, end - pos - 1);
    std::size_t after_line = end + 2;

    if (type == '+' || type == '-') {
        reply.kind = type == '+' ? Reply::Kind::kSimple : Reply::Kind::kError;
        reply.text = std::string(line);
        pos = after_line;
        return ParseStatus::kReply;
    }
    if (type != ':' && type != '$' && type != '*') return ParseStatus::kError;

    long long number = 0;
    const char* last = line.data() + line.size();
    std::from_chars_result parsed = std::from_chars(line.data(), last, number);
    if (line.empty() || parsed.ec != std::errc() || parsed.ptr != last) return ParseStatus::kError;

    if (type == ':') {
        reply.kind = Reply::Kind::kInteger;
        reply.integer = number;
        pos = after_line;
        return ParseStatus::kReply;
    }
    if (number == -1) {  // the null bulk string and the null array
        reply.kind = Reply::Kind::kNull;
        pos = after_line;
        return ParseStatus::kReply;
    }
    if (number < 0) return ParseStatus::kError;

    if (type == '$') {
        if (number > kMaxBulkBytes) return ParseStatus::kError;
        std::size_t size = static_cast<std::size_t>(number);
        if (buf.size() - after_line < size + 2) return ParseStatus::kNeedMore;
        if (buf.substr(after_line + size, 2) != "\r\n") return ParseStatus::kError;
        reply.kind = Reply::Kind::kBulk;
        reply.text = std::string(buf.substr(after_line, size));
        pos = after_line + size + 2;
        return ParseStatus::kReply;
    }

    if (number > kMaxArrayItems) return ParseStatus::kError;
    reply.kind = Reply::Kind::kArray;
    reply.items.clear();
    std::size_t cursor = after_line;
    for (long long i = 0; i < number; ++i) {
        Reply item;
        ParseStatus status = parse_value(buf, cursor, item, depth + 1);
        if (status != ParseStatus::kReply) return status;
        reply.items.push_back(item);
    }
    pos = cursor;
    return ParseStatus::kReply;
}

}  // namespace

std::map<std::string, std::string> Reply::to_map() const {
    std::map<std::string, std::string> result;
    for (std::size_t i = 0; i + 1 < items.size(); i += 2) {
        result[items[i].text] = items[i + 1].text;
    }
    return result;
}

std::string encode_command(const std::vector<std::string>& args) {
    std::string out = "*" + std::to_string(args.size()) + "\r\n";
    for (const std::string& arg : args) {
        out += "$" + std::to_string(arg.size()) + "\r\n" + arg + "\r\n";
    }
    return out;
}

void ReplyParser::feed(std::string_view data) { buffer_.append(data); }

ParseStatus ReplyParser::next(Reply& reply) {
    // shortcut: re-scans from the start after every read; fine for couchd's small replies.
    std::size_t pos = 0;
    Reply parsed;
    ParseStatus status = parse_value(buffer_, pos, parsed, 0);
    if (status == ParseStatus::kReply) {
        reply = parsed;
        buffer_.erase(0, pos);
    }
    return status;
}

}  // namespace couchd
