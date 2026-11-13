-- 2.x 迁移 v32(device-controller-state)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
CREATE TABLE device_controller_state (
  device_id     TEXT NOT NULL,
  data_epoch    TEXT NOT NULL,
  config_scope  TEXT NOT NULL,
  controller_id TEXT NOT NULL,
  -- 控制端自己编的稳定视图键(renderer 草稿布局等),名字复用 deviceIdSchema 的形状约束
  view_key      TEXT NOT NULL,
  revision      INTEGER NOT NULL,
  -- msgpack 编码的 DeviceValue;空值是一个字节的 nil,不是 NULL
  value         BLOB NOT NULL,
  updated_at    INTEGER,
  PRIMARY KEY (device_id, data_epoch, config_scope, controller_id, view_key)
);

-- 一个控制端只有一条推送订阅登记,所以 controller_id 之后没有第二个维度。
-- registration_ref 是不透明引用(见文件头),platform 是投递侧的稳定枚举。
CREATE TABLE device_notification_subscriptions (
  device_id        TEXT NOT NULL,
  data_epoch       TEXT NOT NULL,
  config_scope     TEXT NOT NULL,
  controller_id    TEXT NOT NULL,
  registration_ref TEXT NOT NULL,
  platform         TEXT NOT NULL CHECK(platform IN ('ios','android')),
  enabled          INTEGER,
  revision         INTEGER,
  updated_at       INTEGER,
  PRIMARY KEY (device_id, data_epoch, config_scope, controller_id)
);
