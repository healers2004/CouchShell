#include "protocol/reply_parser.h"

#include <gtest/gtest.h>

#include <map>
#include <string>
#include <vector>

using couchd::ParseStatus;
using couchd::Reply;
using couchd::ReplyParser;

namespace {

ParseStatus parse_once(const std::string& input, Reply& reply) {
    ReplyParser parser;
    parser.feed(input);
    return parser.next(reply);
}

}  // namespace

TEST(ReplyParser, ParsesEveryType) {
    Reply reply;
    ASSERT_EQ(parse_once("+OK\r\n", reply), ParseStatus::kReply);
    EXPECT_EQ(reply.kind, Reply::Kind::kSimple);
    EXPECT_EQ(reply.text, "OK");

    ASSERT_EQ(parse_once("-ERR no\r\n", reply), ParseStatus::kReply);
    EXPECT_EQ(reply.kind, Reply::Kind::kError);

    ASSERT_EQ(parse_once(":-7\r\n", reply), ParseStatus::kReply);
    EXPECT_EQ(reply.integer, -7);

    ASSERT_EQ(parse_once("$4\r\na\r\nb\r\n", reply), ParseStatus::kReply);
    EXPECT_EQ(reply.text, "a\r\nb");

    ASSERT_EQ(parse_once("$-1\r\n", reply), ParseStatus::kReply);
    EXPECT_EQ(reply.kind, Reply::Kind::kNull);
}

TEST(ReplyParser, ArrayBecomesMap) {
    Reply reply;
    ASSERT_EQ(parse_once("*4\r\n$1\r\na\r\n$1\r\n1\r\n$1\r\nb\r\n$1\r\n2\r\n", reply),
              ParseStatus::kReply);
    std::map<std::string, std::string> expected = {{"a", "1"}, {"b", "2"}};
    EXPECT_EQ(reply.to_map(), expected);
}

// TCP can split a reply at any byte, including inside a nested array.
TEST(ReplyParser, SplitAtEveryBytePosition) {
    const std::string input = "*3\r\n$7\r\nmessage\r\n$1\r\nc\r\n:5\r\n";
    for (std::size_t split = 1; split < input.size(); ++split) {
        ReplyParser parser;
        Reply reply;
        parser.feed(input.substr(0, split));
        EXPECT_EQ(parser.next(reply), ParseStatus::kNeedMore) << "split at " << split;
        parser.feed(input.substr(split));
        ASSERT_EQ(parser.next(reply), ParseStatus::kReply) << "split at " << split;
        ASSERT_EQ(reply.items.size(), 3u);
        EXPECT_EQ(reply.items[0].text, "message");
        EXPECT_EQ(reply.items[2].integer, 5);
    }
}

TEST(ReplyParser, TwoRepliesInOneRead) {
    ReplyParser parser;
    Reply reply;
    parser.feed("+A\r\n+B\r\n");
    ASSERT_EQ(parser.next(reply), ParseStatus::kReply);
    EXPECT_EQ(reply.text, "A");
    ASSERT_EQ(parser.next(reply), ParseStatus::kReply);
    EXPECT_EQ(reply.text, "B");
    EXPECT_EQ(parser.next(reply), ParseStatus::kNeedMore);
}

TEST(ReplyParser, RejectsMalformedReplies) {
    const std::vector<std::string> inputs = {
        "?x\r\n", ":abc\r\n", "$-2\r\n", "$99999999999\r\n", "$3\r\nfooXY", "*-5\r\n", "\r\n",
    };
    for (std::size_t i = 0; i < inputs.size(); ++i) {
        Reply reply;
        EXPECT_EQ(parse_once(inputs[i], reply), ParseStatus::kError) << "input #" << i;
    }
}

TEST(ReplyParser, RejectsTooDeepNesting) {
    std::string input;
    for (int i = 0; i < 20; ++i) input += "*1\r\n";
    Reply reply;
    EXPECT_EQ(parse_once(input + ":1\r\n", reply), ParseStatus::kError);
}

TEST(EncodeCommand, UsesByteLengths) {
    EXPECT_EQ(couchd::encode_command({"GET", "foo"}), "*2\r\n$3\r\nGET\r\n$3\r\nfoo\r\n");
}
