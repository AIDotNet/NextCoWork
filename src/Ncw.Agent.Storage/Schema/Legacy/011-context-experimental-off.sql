-- 2.x 迁移 v11(context-experimental-off)。原样取自 2.x 的 src/main/db/schema.ts,只追加、永不修改(D13)。
UPDATE settings
   SET json = json_set(json, '$.contextManagement.experimentalMode', json('false'))
 WHERE id = 1
   AND json_extract(json, '$.contextManagement.experimentalMode') = 1;
