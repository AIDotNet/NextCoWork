-- 3.0 第一条迁移:已配对的控制端(docs/design/09 §3、§4)。
-- 协议是全新设计,2.x 的设备配对表(device_trust 等)不迁移、不复用,原样留着。
-- fingerprint = 控制端证书公钥(SubjectPublicKeyInfo)的 SHA-256,小写十六进制。
-- route_token_hash = 配对时发给控制端的中继路由令牌的 SHA-256(中继上线后使用,现在就随配对保存)。
CREATE TABLE controller_trust (
  fingerprint       TEXT PRIMARY KEY,
  name              TEXT NOT NULL,
  role              TEXT NOT NULL CHECK (role IN ('owner', 'member', 'viewer')),
  paired_at         INTEGER NOT NULL,
  last_seen_at      INTEGER,
  route_token_hash  TEXT NOT NULL
);
