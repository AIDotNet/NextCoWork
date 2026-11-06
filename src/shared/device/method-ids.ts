import { z } from 'zod'

/** 方法参数里的实体 id:与 deviceId 同字符集,避免控制字符/路径分隔符进入存储键。 */
export const idSchema = z.string().min(1).max(128).regex(/^[A-Za-z0-9][A-Za-z0-9_.:-]*$/)
