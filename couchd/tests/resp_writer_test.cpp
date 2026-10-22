#include "protocol/resp_writer.h"

#include <gtest/gtest.h>

#include <string>

TEST(RespWriter, FormatsEachReplyType) {
    EXPECT_EQ(couchd::simple_string_reply("OK"), "+OK\r\n");
    EXPECT_EQ(couchd::error_reply("ERR boom"), "-ERR boom\r\n");
    EXPECT_EQ(couchd::integer_reply(-3), ":-3\r\n");
    EXPECT_EQ(couchd::bulk_reply("bar"), "$3\r\nbar\r\n");
    EXPECT_EQ(couchd::bulk_reply(""), "$0\r\n\r\n");
    EXPECT_EQ(couchd::null_reply(), "$-1\r\n");
}

TEST(RespWriter, BulkReplyKeepsCrlfInside) {
    EXPECT_EQ(couchd::bulk_reply("a\r\nb"), "$4\r\na\r\nb\r\n");
}

// An error that quotes user input must not let that input inject a second reply.
TEST(RespWriter, SingleLineRepliesStripCrlf) {
    EXPECT_EQ(couchd::error_reply("ERR bad 'x\r\n+OK'"), "-ERR bad 'x  +OK'\r\n");
    EXPECT_EQ(couchd::simple_string_reply("a\nb"), "+a b\r\n");
}
