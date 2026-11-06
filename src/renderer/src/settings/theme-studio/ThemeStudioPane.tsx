import { useEffect, useState, type ReactNode } from 'react'
import { Check, Copy, MoreHorizontal, Trash2, Upload } from 'lucide-react'
import type { ThemePreference } from '../../../../shared/domain/settings'
import { IMAGE_THEMES, resolveImageTheme, resolveColorTheme, THEME_TOKENS, type ImageTheme, type ThemeProfile, type ThemeToken, type ThemeWallpaper } from '../../../../shared/domain/theme'
import { createThemeProfile, DEFAULT_PROFILE_ID, DEFAULT_SURFACES, resolveProfile, SURFACE_REGIONS, wallpaperFor } from '../../../../shared/domain/theme-profile'
import { prefixedId } from '../../../../shared/util/id'
import { Button } from '../../components/arc/button/button'
import { Dialog } from '../../components/ui/Dialog'
import { Menu, MenuItem } from '../../components/ui/Menu'
import SegmentedControl from '../../components/arc/segmented-control/segmented-control'
import { TextInput } from '../../components/ui/TextInput'
import { useI18n, type TranslationKey } from '../../i18n'
import { builtinThemeNameKey } from '../../i18n/themes'
import { cn } from '../../lib/cn'
import { updateSettings } from '../../services/app'
import { deleteProfile, saveProfile } from '../../services/theme'
import { useImageThemes } from '../../stores/imageTheme'
import { themeDraftDirty, useThemeProfiles } from '../../stores/themeProfiles'
import { useAppearance } from '../../theme/useAppearance'
import type { SettingsPageProps } from '../props'
import { DesktopPreview } from './DesktopPreview'

type Group = 'wallpaper' | 'colors' | 'surfaces' | 'typeMotion' | 'readability'
const GROUPS: Group[] = ['wallpaper', 'colors', 'surfaces', 'typeMotion', 'readability']
const COMMON_TOKENS: ThemeToken[] = ['canvas', 'surface', 'chrome', 'surface-raised', 'tint', 'accent', 'accent-fg', 'fg', 'fg-muted', 'icon', 'danger']

export function ThemeStudioPane({ settings, patch }: Omit<SettingsPageProps, 'sub'>): ReactNode {
  const { t, locale } = useI18n()
  const studio = useThemeProfiles()
  const images = useImageThemes((s) => s.uploaded)
  const appearance = useAppearance()
  const [group, setGroup] = useState<Group>('wallpaper')
  const [category, setCategory] = useState<'current' | 'builtin' | 'mine' | 'assets'>('current')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState(false)
  const [pending, setPending] = useState<(() => void) | null>(null)
  const [deletion, setDeletion] = useState<{ id: string; asset: boolean } | null>(null)
  const [colorScope, setColorScope] = useState<'tokens' | 'light' | 'dark'>('tokens')
  const [keepColors, setKeepColors] = useState(true)
  const [region, setRegion] = useState<(typeof SURFACE_REGIONS)[number]>('canvas')
  const profile = studio.draft
  const active = studio.profiles.find((p) => p.id === settings.activeThemeProfileId) ?? studio.profiles.find((p) => p.id === DEFAULT_PROFILE_ID)
  const dirty = themeDraftDirty()

  useEffect(() => { if (profile === null && active) studio.edit(active) }, [profile, active, studio.edit])
  const run = async (action: () => Promise<void>): Promise<void> => {
    setBusy(true); setError(false)
    try { await action() } catch { setError(true) } finally { setBusy(false) }
  }
  const choose = (p: ThemeProfile): void => {
    const action = (): void => studio.edit(p)
    if (dirty) setPending(() => action); else action()
  }
  const update = (next: Partial<ThemeProfile>): void => { if (profile) studio.update({ ...profile, ...next }) }
  const changeWallpaper = (image: ImageTheme | null): void => {
    if (!profile) return
    const wallpaper = image ? { ...(profile.wallpaper ?? wallpaperFor(image.id)), assetId: image.id, thumbnailAssetId: image.id } : null
    const palette = { ...profile.palette, source: 'auto' as const, seed: image?.seed ?? profile.palette.seed,
      ...(keepColors ? {} : { tokens: {}, light: {}, dark: {} }) }
    update({ wallpaper, palette })
  }
  const upload = (): void => { void run(async () => {
    const image = await useImageThemes.getState().importOne()
    if (!image) return
    const p = createThemeProfile(prefixedId('theme'), image.name)
    p.wallpaper = wallpaperFor(image.id); p.palette = { source: 'auto', seed: image.seed, tokens: {} }
    if (dirty) setPending(() => () => { studio.edit(p); studio.update({ ...p, name: image.name }); useThemeProfiles.setState({ baseline: '' }) })
    else { studio.edit(p); useThemeProfiles.setState({ baseline: '' }) }
    setCategory('mine'); setGroup('wallpaper')
  }) }
  const save = (): void => { if (profile) void run(async () => {
    const originalName = studio.profiles.find((p) => p.id === profile.id)?.name
    const saved = { ...profile, id: profile.builtin ? prefixedId('theme') : profile.id,
      builtin: false, name: (profile.builtin && profile.name === originalName ? nameOf(profile) : profile.name).trim() || t('themeStudio.untitled'), updatedAt: Date.now() }
    const profiles = await saveProfile(saved)
    await updateSettings({ activeThemeProfileId: saved.id })
    useThemeProfiles.setState({ profiles }); studio.edit(profiles.find((p) => p.id === saved.id) ?? saved)
    setCategory('mine')
  }) }
  const duplicate = (p: ThemeProfile): void => { void run(async () => {
    const copy = { ...structuredClone(p), id: prefixedId('theme'), name: t('themeStudio.copyName', { name: nameOf(p) }), builtin: false, createdAt: Date.now() }
    const profiles = await saveProfile(copy); useThemeProfiles.setState({ profiles }); choose(copy); setCategory('mine')
  }) }
  const remove = (): void => { if (deletion) void run(async () => {
    if (deletion.asset) await useImageThemes.getState().remove(deletion.id)
    else useThemeProfiles.setState({ profiles: await deleteProfile(deletion.id) })
    if (profile?.id === deletion.id || profile?.wallpaper?.assetId === deletion.id) studio.discard()
    setDeletion(null)
  }) }
  const nameOf = (p: ThemeProfile): string => {
    if (p.id === DEFAULT_PROFILE_ID) return t('themeStudio.default')
    const imageId = p.wallpaper?.assetId
    const imageKey = p.builtin && imageId ? builtinThemeNameKey('image', imageId) : null
    if (imageKey) return t(imageKey)
    const colorId = p.palette.base?.id
    const colorKey = p.builtin && colorId ? builtinThemeNameKey('color', colorId) : null
    return colorKey ? t(colorKey) : p.name || t('themeStudio.untitled')
  }
  if (!profile) return <p role="status" className="p-6 text-fg-muted">{t('themeStudio.loading')}</p>
  const image = resolveImageTheme(profile.wallpaper?.assetId ?? null, images)
  const originalName = studio.profiles.find((p) => p.id === profile.id)?.name
  const displayName = profile.builtin && profile.name === originalName ? nameOf(profile) : profile.name
  const seedColor = !profile.wallpaper && profile.palette.base ? resolveColorTheme(profile.palette.base)[appearance].accent : profile.palette.seed
  const palette = resolveProfile(profile, colorScope === 'tokens' ? appearance : colorScope)
  const material = profile.surfaces[region]
  const setWallpaper = (next: Partial<ThemeWallpaper>): void => { if (profile.wallpaper) update({ wallpaper: { ...profile.wallpaper, ...next } }) }
  const resetGroup = (): void => {
    const defaults = createThemeProfile(profile.id, profile.name)
    if (group === 'wallpaper') update({ wallpaper: profile.wallpaper ? wallpaperFor(profile.wallpaper.assetId) : null })
    if (group === 'colors') update({ palette: { ...profile.palette, tokens: {}, light: {}, dark: {} } })
    if (group === 'surfaces') update({ surfaces: structuredClone(DEFAULT_SURFACES) })
    if (group === 'typeMotion') update({ typography: defaults.typography, motion: defaults.motion })
    if (group === 'readability') update({ readability: defaults.readability })
  }
  const visible = studio.profiles.filter((p) => category === 'builtin' ? p.builtin : category === 'mine' ? !p.builtin : p.id === active?.id)
  return <div className="theme-studio flex min-h-0 flex-1 flex-col gap-3 py-3">
    <header className="flex flex-wrap items-center justify-between gap-3">
      <div><h2 className="text-[18px] font-semibold text-fg">{t('themeStudio.title')}</h2><p className="mt-1 text-[12px] text-fg-muted">{t('themeStudio.hint')}</p></div>
      <div className="flex gap-2"><Button type="button" variant="secondary" disabled={busy || !dirty} onClick={() => active && studio.edit(active)}>{t('common.cancel')}</Button><Button type="button" variant="primary" disabled={busy || (!dirty && profile.id === active?.id)} onClick={save}>{t('themeStudio.saveApply')}</Button></div>
    </header>
    {error && <p role="alert" className="text-[12px] text-danger">{t('themeStudio.error')}</p>}
    <div className="flex flex-wrap items-center justify-between gap-2 border-b border-hairline pb-3">
      <TextInput ariaLabel={t('themeStudio.name')} value={displayName} onChange={(value) => update({ name: value.slice(0, 80) })} className="max-w-[240px]" />
      <SegmentedControl label={t('preference.appearance')} value={settings.theme} options={(['system', 'light', 'dark'] as const).map((value) => ({ value, label: t(`preference.${value}`) }))} onValueChange={(theme) => patch({ theme: theme as ThemePreference })} />
    </div>
    <div className="theme-studio-layout">
      <nav className="theme-studio-library" aria-label={t('themeStudio.library')}>
        <div className="mb-3 grid grid-cols-2 gap-1">{(['current', 'builtin', 'mine', 'assets'] as const).map((c) => <button type="button" key={c} aria-pressed={category === c} onClick={() => setCategory(c)} className={cn('rounded-lg px-2 py-2 text-[11px]', category === c ? 'bg-tint text-fg' : 'text-fg-muted hover:bg-tint-hover')}>{t(`themeStudio.category.${c}`)}</button>)}</div>
        <div className="space-y-3">{category === 'assets' ? images.map((asset) => <div key={asset.id}>
          <button type="button" onClick={() => changeWallpaper(asset)} className="w-full text-left"><AssetImage image={asset} /><span className="mt-1 block truncate text-[12px]">{asset.name}</span></button>
          <p className="text-[10px] text-fg-muted">{asset.width && asset.height ? `${asset.width} × ${asset.height}` : ''} {asset.bytes ? `${(asset.bytes / 1048576).toFixed(1)} MB` : ''}</p>
          {asset.unavailable && <p className="text-[11px] text-danger">{t('themeStudio.unavailable')}</p>}
          <button type="button" className="text-[11px] text-danger" onClick={() => setDeletion({ id: asset.id, asset: true })}>{t('common.delete')}</button>
        </div>) : visible.map((p) => <article key={p.id} className={cn('rounded-lg border p-1.5', p.id === profile.id ? 'border-accent' : 'border-hairline')}>
          <button type="button" onClick={() => choose(p)} className="block w-full text-left" aria-pressed={p.id === profile.id}>
            <ProfilePreview profile={p} images={images} />
            <span className="mt-1.5 flex items-center gap-1.5 text-[12px]"><span className="size-2 shrink-0 rounded-full" style={{ backgroundColor: resolveProfile(p, appearance).tokens.accent }} /><span className="truncate">{nameOf(p)}</span>{p.id === active?.id && <Check size={12} className="ml-auto shrink-0 text-accent" aria-label={t('themeStudio.active')} />}</span>
          </button>
          <div className="mt-1 flex items-center justify-between text-[10px] text-fg-muted"><span>{t('themeStudio.bothModes')}{p.updatedAt > 0 && ` · ${new Date(p.updatedAt).toLocaleDateString(locale)}`}</span>
            <Menu label={t('themeStudio.more')} trigger={<MoreHorizontal size={14} />} triggerClassName="rounded p-1 hover:bg-tint" width={150}>{(close) => <>
              <MenuItem icon={<Copy size={12} />} onSelect={() => { close(); duplicate(p) }}>{t('themeStudio.duplicate')}</MenuItem>
              {!p.builtin && <MenuItem onSelect={() => { close(); choose(p) }}>{t('themeStudio.rename')}</MenuItem>}
              {!p.builtin && <MenuItem icon={<Trash2 size={12} />} onSelect={() => { close(); setDeletion({ id: p.id, asset: false }) }}>{t('common.delete')}</MenuItem>}
            </>}</Menu>
          </div>
        </article>)}</div>
        <Button type="button" variant="secondary" className="mt-3 w-full" disabled={busy} onClick={upload}><Upload size={13} />{t('themeStudio.upload')}</Button>
      </nav>
      <section className="theme-studio-center min-w-0"><DesktopPreview profile={profile} images={images} />
        <div className="mt-3 flex flex-wrap items-center justify-between gap-2 text-[11px] text-fg-muted"><span>{dirty ? t('themeStudio.unsaved') : t('themeStudio.saved')}</span><Button type="button" variant="secondary" onClick={() => useThemeProfiles.setState({ fullPreview: !studio.fullPreview })}>{t(studio.fullPreview ? 'themeStudio.exitFull' : 'themeStudio.fullDesktop')}</Button></div>
        {palette.warnings.length > 0 && <p role="status" className="mt-2 text-[11px] text-danger">{t('themeStudio.contrastWarning', { count: palette.warnings.length })}</p>}
      </section>
      <section className="theme-studio-inspector min-w-0 border-l border-hairline pl-3">
        <div className="mb-3 flex flex-wrap gap-1">{GROUPS.map((g) => <button type="button" key={g} aria-pressed={group === g} className={cn('rounded-md px-2 py-1.5 text-[11px]', group === g ? 'bg-tint text-fg' : 'text-fg-muted hover:bg-tint-hover')} onClick={() => setGroup(g)}>{t(`themeStudio.${g}`)}</button>)}</div>
        <div className="space-y-3">
          {group === 'wallpaper' && <>
            <select aria-label={t('themeStudio.wallpaperAsset')} className="studio-select apple-field" value={profile.wallpaper?.assetId ?? ''} onChange={(e) => changeWallpaper(resolveImageTheme(e.target.value, images))}><option value="">{t('themeStudio.noWallpaper')}</option>{[...IMAGE_THEMES, ...images].map((i) => {
              const key = i.source.kind === 'builtin' ? builtinThemeNameKey('image', i.id) : null
              return <option value={i.id} key={i.id}>{key ? t(key) : i.name}</option>
            })}</select>
            <CheckField label={t('themeStudio.keepColors')} checked={keepColors} onChange={setKeepColors} />
            {profile.wallpaper && <>
              <div className="relative touch-none overflow-hidden rounded-lg" onPointerDown={(e) => { e.currentTarget.setPointerCapture(e.pointerId); const rect = e.currentTarget.getBoundingClientRect(); setWallpaper({ position: { x: Math.max(0, Math.min(100, (e.clientX - rect.left) / rect.width * 100)), y: Math.max(0, Math.min(100, (e.clientY - rect.top) / rect.height * 100)) } }) }} onPointerMove={(e) => { if (!e.currentTarget.hasPointerCapture(e.pointerId)) return; const rect = e.currentTarget.getBoundingClientRect(); setWallpaper({ position: { x: Math.max(0, Math.min(100, (e.clientX - rect.left) / rect.width * 100)), y: Math.max(0, Math.min(100, (e.clientY - rect.top) / rect.height * 100)) } }) }}>
                <AssetImage image={image} /><span className="pointer-events-none absolute size-4 -translate-x-1/2 -translate-y-1/2 rounded-full border-2 border-white bg-accent shadow" style={{ left: `${profile.wallpaper.position.x}%`, top: `${profile.wallpaper.position.y}%` }} />
              </div>
              {image?.unavailable && <p className="text-[11px] text-danger">{t('themeStudio.unavailable')}</p>}
              <Button type="button" variant="secondary" onClick={() => setWallpaper({ position: { x: 50, y: 50 } })}>{t('themeStudio.autoFocus')}</Button>
              <SelectField label={t('themeStudio.fit')} value={profile.wallpaper.fit} options={['cover', 'contain']} prefix="themeStudio.fit" onChange={(fit) => setWallpaper({ fit })} />
              <SelectField label={t('themeStudio.crop')} value={profile.wallpaper.crop} options={['original', '16:9', '4:3', '1:1']} prefix="themeStudio.crop" onChange={(crop) => setWallpaper({ crop })} />
              <Range label={t('themeStudio.zoom')} value={profile.wallpaper.scale} min={1} max={3} onChange={(scale) => setWallpaper({ scale })} />
              <Range label={t('themeStudio.positionX')} value={profile.wallpaper.position.x} min={0} max={100} step={1} onChange={(x) => setWallpaper({ position: { ...profile.wallpaper!.position, x } })} />
              <Range label={t('themeStudio.positionY')} value={profile.wallpaper.position.y} min={0} max={100} step={1} onChange={(y) => setWallpaper({ position: { ...profile.wallpaper!.position, y } })} />
              <Range label={t('themeStudio.opacity')} value={profile.wallpaper.opacity} onChange={(opacity) => setWallpaper({ opacity })} />
              <Range label={t('themeStudio.blur')} value={profile.wallpaper.blur} max={80} step={1} onChange={(blur) => setWallpaper({ blur, render: blur > 0 ? 'blur' : 'overlay' })} />
              <Range label={t('themeStudio.brightness')} value={profile.wallpaper.brightness} min={.2} max={2} onChange={(brightness) => setWallpaper({ brightness })} />
              <Range label={t('themeStudio.saturation')} value={profile.wallpaper.saturation} max={2} onChange={(saturation) => setWallpaper({ saturation })} />
              <SelectField label={t('themeStudio.scope')} value={profile.wallpaper.scope} options={['desktop', 'workspace']} prefix="themeStudio.scope" onChange={(scope) => setWallpaper({ scope })} />
              <SelectField label={t('themeStudio.positioning')} value={profile.wallpaper.positioning} options={['viewport', 'region']} prefix="themeStudio.positioning" onChange={(positioning) => setWallpaper({ positioning })} />
              <SelectField label={t('themeStudio.animation')} value={profile.wallpaper.animation} options={['auto', 'static']} prefix="themeStudio.animation" onChange={(animation) => setWallpaper({ animation })} />
            </>}
          </>}
          {group === 'colors' && <>
            <p className="text-[11px] text-fg-muted">{t('themeStudio.colorHint')}</p>
            <SelectField label={t('themeStudio.colorScope')} value={colorScope} options={['tokens', 'light', 'dark']} prefix="themeStudio.colorScope" onChange={setColorScope} />
            <label className="flex items-center justify-between text-[12px]">{t('themeStudio.seed')}<input type="color" aria-label={t('themeStudio.seed')} value={seedColor} onChange={(e) => update({ palette: { ...profile.palette, source: 'manual', seed: e.target.value, base: undefined } })} /></label>
            <Button type="button" variant="secondary" onClick={() => update({ palette: { ...profile.palette, source: 'auto', seed: image?.seed ?? profile.palette.seed, tokens: {}, light: {}, dark: {} } })}>{t('themeStudio.autoColors')}</Button>
            {[COMMON_TOKENS, THEME_TOKENS.filter((k) => !COMMON_TOKENS.includes(k))].map((keys, i) => {
              const controls = <div className="space-y-2">{keys.map((key) => <label key={key} className="flex items-center justify-between gap-2 text-[11px] text-fg-muted"><span>{t(`themeStudio.token.${key}` as TranslationKey)}</span><input type="color" aria-label={t(`themeStudio.token.${key}` as TranslationKey)} value={profile.palette[colorScope]?.[key] ?? palette.tokens[key]} onChange={(e) => update({ palette: { ...profile.palette, [colorScope]: { ...profile.palette[colorScope], [key]: e.target.value } } })} /></label>)}</div>
              return i === 0 ? <div key={i}>{controls}</div> : <details key={i}><summary className="mb-2 cursor-pointer text-[12px]">{t('themeStudio.advancedTokens')}</summary>{controls}</details>
            })}
          </>}
          {group === 'surfaces' && <>
            <p className="text-[11px] text-fg-muted">{t('themeStudio.surfaceHint')}</p>
            <SelectField label={t('themeStudio.region')} value={region} options={SURFACE_REGIONS} prefix="themeStudio.region" onChange={setRegion} />
            <Range label={t('themeStudio.opacity')} value={material.wallpaper} onChange={(wallpaper) => update({ surfaces: { ...profile.surfaces, [region]: { ...material, wallpaper } } })} />
            <Range label={t('themeStudio.surfaceOpacity')} value={material.opacity} onChange={(opacity) => update({ surfaces: { ...profile.surfaces, [region]: { ...material, opacity } } })} />
            <Range label={t('themeStudio.mask')} value={material.mask} onChange={(mask) => update({ surfaces: { ...profile.surfaces, [region]: { ...material, mask } } })} />
            <CheckField label={t('themeStudio.solid')} checked={material.solid} onChange={(solid) => update({ surfaces: { ...profile.surfaces, [region]: { ...material, solid } } })} />
            <CheckField label={t('themeStudio.inheritBlur')} checked={material.blur} onChange={(blur) => update({ surfaces: { ...profile.surfaces, [region]: { ...material, blur } } })} />
          </>}
          {group === 'typeMotion' && <>
            <SelectField label={t('themeStudio.uiFont')} value={profile.typography.uiFont} options={['system', 'system-rounded', 'system-serif']} prefix="themeStudio.fontChoice" onChange={(uiFont) => update({ typography: { ...profile.typography, uiFont } })} />
            <SelectField label={t('themeStudio.uiScale')} value={profile.typography.scale} options={['small', 'standard', 'large']} prefix="themeStudio.scale" onChange={(scale) => update({ typography: { ...profile.typography, scale } })} />
            <SelectField label={t('themeStudio.weight')} value={profile.typography.weight} options={['standard', 'compact', 'comfortable']} prefix="themeStudio.weight" onChange={(weight) => update({ typography: { ...profile.typography, weight } })} />
            <SelectField label={t('themeStudio.codeFont')} value={profile.typography.codeFont} options={['system-mono']} prefix="themeStudio.codeFont" onChange={(codeFont) => update({ typography: { ...profile.typography, codeFont } })} />
            <SelectField label={t('themeStudio.motion')} value={profile.motion.level} options={['standard', 'soft', 'reduced', 'off']} prefix="themeStudio.motion" onChange={(level) => update({ motion: { level } })} />
            <p className="text-[11px] text-fg-muted">{t('themeStudio.systemMotion')}</p>
          </>}
          {group === 'readability' && <>
            <CheckField label={t('themeStudio.guardrails')} checked={profile.readability.guardrails} onChange={(guardrails) => update({ readability: { ...profile.readability, guardrails } })} />
            <SelectField label={t('themeStudio.textContrast')} value={profile.readability.textContrast} options={['auto', 'strict', 'relaxed']} prefix="themeStudio.textContrast" onChange={(textContrast) => update({ readability: { ...profile.readability, textContrast } })} />
            <CheckField label={t('themeStudio.allowLowContrast')} checked={profile.readability.allowLowContrast} onChange={(allowLowContrast) => update({ readability: { ...profile.readability, allowLowContrast } })} />
            {(!profile.readability.guardrails || profile.readability.allowLowContrast) && <p className="text-[11px] text-danger">{t('themeStudio.protectionWarning')}</p>}
            <p className="text-[11px] text-fg-muted">{t('themeStudio.contrastHint')}</p>
            {palette.warnings.map((w) => <p key={`${w.token}-${w.background}`} className="text-[10px] text-danger">{t('themeStudio.contrastPair', { token: w.token, background: w.background, ratio: w.ratio.toFixed(2), target: w.target })}</p>)}
          </>}
          <div className="flex flex-wrap gap-1 border-t border-hairline pt-3"><Button type="button" variant="secondary" onClick={resetGroup}>{t('themeStudio.resetGroup')}</Button><Button type="button" variant="secondary" onClick={() => update(createThemeProfile(profile.id, profile.name))}>{t('themeStudio.restoreDefault')}</Button></div>
        </div>
      </section>
    </div>
    <Dialog open={pending !== null} title={t('themeStudio.unsaved')} onClose={() => setPending(null)} footer={<><Button type="button" variant="secondary" onClick={() => setPending(null)}>{t('common.cancel')}</Button><Button type="button" variant="secondary" onClick={() => { pending?.(); setPending(null) }}>{t('themeStudio.discard')}</Button></>}><p>{t('themeStudio.discardHint')}</p></Dialog>
    <Dialog open={deletion !== null} title={t('themeStudio.deleteTitle')} onClose={() => setDeletion(null)} footer={<><Button type="button" variant="secondary" onClick={() => setDeletion(null)}>{t('common.cancel')}</Button><Button type="button" variant="secondary" disabled={busy} onClick={remove}>{t('common.delete')}</Button></>}><p>{t(deletion?.asset ? 'themeStudio.deleteAssetHint' : 'themeStudio.deleteHint')}</p></Dialog>
  </div>
}

function AssetImage({ image, seed = '#527969' }: { image: ImageTheme | null; seed?: string }): ReactNode {
  return <div className="aspect-[16/9] w-full overflow-hidden rounded-md" style={{ backgroundColor: image?.seed ?? seed, backgroundImage: image?.source.kind === 'builtin' ? image.source.css : undefined }}>
    {image?.thumbnailUrl && <img src={image.thumbnailUrl} alt="" loading="lazy" className="h-full w-full object-cover" />}
  </div>
}
function ProfilePreview({ profile, images }: { profile: ThemeProfile; images: readonly ImageTheme[] }): ReactNode {
  const appearance = useAppearance()
  const image = resolveImageTheme(profile.wallpaper?.assetId ?? null, images)
  if (image !== null) return <AssetImage image={image} seed={profile.palette.seed} />
  const colors = resolveProfile(profile, appearance).tokens
  return <div className="aspect-[16/9] w-full overflow-hidden rounded-md" style={{ background: `linear-gradient(135deg, ${colors.canvas}, ${colors.accent})` }} />
}
function CheckField({ label, checked, onChange }: { label: string; checked: boolean; onChange: (v: boolean) => void }): ReactNode {
  return <label className="flex items-center justify-between gap-2 text-[12px] text-fg"><span>{label}</span><input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} className="accent-accent" /></label>
}
function Range({ label, value, onChange, min = 0, max = 1, step = .01 }: { label: string; value: number; onChange: (v: number) => void; min?: number; max?: number; step?: number }): ReactNode {
  return <label className="block text-[11px] text-fg-muted"><span className="flex justify-between"><span>{label}</span><output>{Number(value.toFixed(2))}</output></span><input type="range" aria-label={label} value={value} min={min} max={max} step={step} onChange={(e) => onChange(Number(e.target.value))} className="mt-1 w-full accent-accent" /></label>
}
function SelectField<T extends string>({ label, value, options, prefix, onChange }: { label: string; value: T; options: readonly T[]; prefix: string; onChange: (value: T) => void }): ReactNode {
  const { t } = useI18n()
  return <label className="block text-[11px] text-fg-muted">{label}<select className="studio-select mt-1" value={value} onChange={(e) => onChange(e.target.value as T)}>{options.map((o) => <option key={o} value={o}>{t(`${prefix}.${o}` as TranslationKey)}</option>)}</select></label>
}
