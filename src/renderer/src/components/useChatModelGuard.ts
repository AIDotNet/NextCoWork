/**
 * 对话模型选择器的「打开时校正」守卫。
 *
 * 需求:所有选**对话模型**的选择器(输入框、通用页五个、工作区默认模型、Hooks)
 * 只列文本模型(见 `isChatModelAlias`);存量配置里已经选中图片模型的,**打开选择器
 * 时**就地换成第一个文本模型,并 toast 告诉用户换成了什么。
 *
 * 为什么是「打开时」而不是启动时全局改写:全局改写会在用户毫不知情时动他的配置,
 * 而图片模型是**他自己选过的**;打开选择器的那一刻他正在看这份配置,就地校正
 * 加一句提示是他唯一不会觉得「配置自己变了」的时机。
 *
 * 为什么抽成 hook:六个选择器散在四个文件里,逐处抄「判据 → 换 → 提示」三步的
 * 症状是只有一两处带着提示,其余静默换模型 —— 那正是「配置自己变了」。
 * 这里是唯一收口;判据本身在 shared 的 `chatModelCorrection`(纯函数,有测试)。
 *
 * 用法:`onOpenChange={guard}`(ProviderModelMenu / Select 都收这个形状的回调)。
 */
import type { ModelAlias, UpstreamProvider } from '../../../shared/domain/provider'
import { chatModelCorrection } from '../../../shared/domain/model-selection'
import { useI18n } from '../i18n'
import { toast } from '../stores/toast'

export function useChatModelGuard(
  models: readonly ModelAlias[],
  providers: readonly UpstreamProvider[],
  current: { model: string; modelProviderId: string | undefined },
  /** 别名与供应商一起给(见 `model-selection.ts` 的成对决定说明) */
  onChange: (model: string, modelProviderId: string) => void
): (open: boolean) => void {
  const { t } = useI18n()
  return (open: boolean) => {
    if (!open) return
    const fix = chatModelCorrection(models, providers, current.model, current.modelProviderId)
    if (fix === null) return
    onChange(fix.alias, fix.modelProviderId)
    toast.info(t('imageGen.notChatModel', { model: fix.alias }))
  }
}
