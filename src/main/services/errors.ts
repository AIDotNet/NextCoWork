import { DeviceProtocolError, type DeviceError } from '../../shared/device/errors'

/** 未知异常可能包含 SQL、文件路径或凭据；只返回稳定的公开错误。 */
export function toDeviceError(error: unknown): DeviceError {
  if (!(error instanceof DeviceProtocolError) || error.code === 'internal') {
    return { code: 'internal', message: 'Device operation failed', retryable: false }
  }
  return {
    code: error.code,
    message: Array.from(error.message).slice(0, 256).join(''),
    retryable: ['busy', 'disconnected', 'timeout', 'resync_required'].includes(error.code)
  }
}
