-- 助手消息的来源(api / provider / 上游模型)和停止原因:3.0 的跨模型切换(只回放同源的思考块)和「中止的回复回放时整条跳过」要用,
-- 2.x 没有这些信息。可空 JSON 文本,Storage 原文进出不解释;2.x 写的旧消息保持 NULL(= 来源未知)。2.x 读这张表时忽略多出来的列。
ALTER TABLE messages ADD COLUMN meta TEXT;
