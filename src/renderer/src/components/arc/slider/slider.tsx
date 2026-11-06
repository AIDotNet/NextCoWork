"use client";

import { useEffect, useId, useLayoutEffect, useRef, useState, type FocusEvent, type KeyboardEvent, type PointerEvent, type ReactNode } from "react";
import { AnimatePresence, animate, motion, useMotionValue, useReducedMotion, useTransform, type MotionStyle, type MotionValue, type Variants } from "motion/react";
import { motionTokens } from "../lib/motion-tokens";
import styles from "./slider.module.css";

export type SliderValue = number | [number, number];
export interface SliderMark { value: number; label?: string; }

export interface SliderProps<T extends SliderValue = number> {
  /** Visible label. Range thumbs are named from it unless `thumbLabels` says otherwise. */
  label: string;
  /** A number for one thumb, a pair for a range. */
  value?: T;
  defaultValue?: T;
  onValueChange?: (value: T) => void;
  /** Runs once a drag is released or a key changes the value, for work too heavy for every step. */
  onValueCommit?: (value: T) => void;
  min?: number;
  max?: number;
  step?: number;
  /** PageUp, PageDown, and Shift with an arrow move this far. Defaults to a tenth of the range. */
  largeStep?: number;
  /** Ticks on the track. A number draws a tick; a mark with a label also prints it under the track, and clicking it moves there. */
  marks?: (number | SliderMark)[];
  /** The closest two range thumbs may sit, in steps. */
  minStepsBetweenThumbs?: number;
  /** Formats the readout, the bubble, and the spoken value. */
  format?: (value: number) => string;
  /** Show the value beside the label. The bubble over the thumb still appears while dragging or focused. */
  showValue?: boolean;
  /** Accessible names for the two range thumbs. */
  thumbLabels?: [string, string];
  /** Content beside the track, such as an icon or a mute button. */
  start?: ReactNode;
  end?: ReactNode;
  /** Submits the value with a form, one hidden input per thumb. */
  name?: string;
  disabled?: boolean;
  className?: string;
}

type Part = { key: string; digit: number } | { key: string; text: string };
type Drag = { pointer: number; index: number | null; grab: number; x: number; raw: number; samples: { t: number; p: number }[] };

const { spring, duration, blur } = motionTokens;
const enterEase = [...motionTokens.ease.enter] as [number, number, number, number];
const exitEase = [...motionTokens.ease.standard] as [number, number, number, number];
const DIGITS = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];
/** Motion drops velocity on time-defined springs, so anything a gesture hands off to runs the same spring written as stiffness and damping.
 *  Positions are percentages of the track, so the rest thresholds are set far below a pixel. */
const physical = ({ visualDuration, bounce }: { visualDuration: number; bounce: number }) => { const root = (2 * Math.PI) / (visualDuration * 1.2); return { type: "spring" as const, stiffness: root * root, damping: 2 * (1 - bounce) * root, restDelta: .002, restSpeed: .02 }; };
const thumbSpring = physical(spring.snappy);
const kickSpring = physical(spring.morph);
/** Pixels of rubber-band travel past a limit, the velocity of a key press at a limit, and the pixel size of a step that release momentum may choose. */
const STRETCH = 9, BUMP_PX = 150, SNAP_PX = 12;
/** iOS style resistance: travel past a limit gives less and less, and never more than `limit` pixels. */
const rubber = (distance: number, limit = STRETCH) => Math.sign(distance) * (1 - 1 / (Math.abs(distance) * .55 / limit + 1)) * limit;
/** Where a release is heading, from a scroll-like deceleration (rate .99), in the same units as the velocity. */
const project = (velocity: number) => velocity * .099;
const decimalsOf = (value: number) => (String(value).split(".")[1] ?? "").length;
const clamp = (value: number, low: number, high: number) => Math.min(high, Math.max(low, value));
const isDigit = (char: string) => char >= "0" && char <= "9";

/** Digits are keyed by place value and the text around them by side, so $950 → $1,000 opens a column and a comma while the rest roll. */
function partsOf(text: string): Part[] {
  const chars = [...text];
  const first = chars.findIndex(isDigit);
  const last = chars.length - 1 - [...chars].reverse().findIndex(isDigit);
  let place = chars.filter(isDigit).length;
  return chars.map((char, index): Part => {
    if (first < 0 || index < first) return { key: `p${index}${char}`, text: char };
    if (index > last) return { key: `s${chars.length - index}${char}`, text: char };
    if (isDigit(char)) return { key: `d${--place}`, digit: Number(char) };
    return { key: `g${place}${char}`, text: char };
  });
}

/* A new column opens its width while it rises in the direction of change; a leaving one closes on a spring that never passes zero. */
const slot: Variants = {
  enter: (direction: number) => ({ width: 0, scale: .6, opacity: 0, y: `${direction * .3}em`, filter: `blur(${blur.soft}px)` }),
  center: { width: "auto", scale: 1, opacity: 1, y: 0, filter: "blur(0px)", transitionEnd: { filter: "none" }, transition: { width: spring.morph, scale: spring.morph, opacity: spring.morph, y: spring.snappy, filter: { duration: duration.standard, ease: enterEase } } },
  exit: (direction: number) => ({ width: 0, scale: .6, opacity: 0, y: `${direction * -.3}em`, filter: `blur(${blur.subtle}px)`, transition: { width: spring.smooth, scale: spring.smooth, y: { duration: duration.fast, ease: exitEase }, opacity: { duration: duration.instant }, filter: { duration: duration.instant } } }),
};
/* Reduced motion keeps a short fade and no travel. Its resting state matches `slot`, so either branch hydrates the same markup. */
const still: Variants = { enter: { width: "auto", scale: 1, opacity: 0, y: 0, filter: "none" }, center: { width: "auto", scale: 1, opacity: 1, y: 0, filter: "none", transition: { duration: duration.instant } }, exit: { width: 0, opacity: 0, transition: { duration: 0 } } };

/** One digit on the wheel. Its distance from the wheel position sets where it sits, how clear it is, and whether it shows. */
function Glyph({ position, digit }: { position: MotionValue<number>; digit: number }) {
  const offset = (current: number) => ((((digit - current) % 10) + 15) % 10) - 5;
  const y = useTransform(position, current => `${offset(current) * 1.05}em`);
  const opacity = useTransform(position, current => Math.max(0, 1 - Math.abs(offset(current)) ** 1.5 * 1.1));
  const visibility = useTransform(position, current => Math.abs(offset(current)) >= 1 ? "hidden" : "visible");
  const filter = useTransform(position, current => { const distance = Math.abs(offset(current)); return distance < .02 || distance >= 1 ? "none" : `blur(${(distance * blur.soft * .75).toFixed(2)}px)`; });
  return <motion.span className={styles.glyph} style={{ y, opacity, filter, visibility }}>{digit}</motion.span>;
}

/** An odometer wheel. It turns the way the value moved, wraps 9 → 0, and retargets mid spin while a drag keeps it busy. */
function Wheel({ digit, direction }: { digit: number; direction: number }) {
  const reduced = useReducedMotion();
  const position = useMotionValue(digit);
  const wheel = useRef({ digit, target: digit });
  useLayoutEffect(() => {
    const state = wheel.current;
    if (state.digit === digit) return;
    state.target += direction > 0 ? (digit - state.digit + 10) % 10 : -((state.digit - digit + 10) % 10);
    state.digit = digit;
    if (reduced) position.jump(state.target);
    else animate(position, state.target, thumbSpring);
  }, [digit, direction, position, reduced]);
  return <><span className={styles.sizer}>0</span>{DIGITS.map(item => <Glyph key={item} position={position} digit={item} />)}</>;
}

/** A formatted value whose digits roll in the direction it moved and whose width follows new columns on a spring. */
function RollingNumber({ value, text }: { value: number; text: string }) {
  const reduced = useReducedMotion();
  const [trail, setTrail] = useState({ value, direction: 1 });
  if (trail.value !== value) setTrail({ value, direction: value > trail.value ? 1 : -1 });
  const direction = trail.value === value ? trail.direction : value > trail.value ? 1 : -1;
  return <span className={styles.number}><AnimatePresence initial={false} custom={direction}>{partsOf(text).map(part => <motion.span key={part.key} className={"digit" in part ? styles.column : styles.symbol} custom={direction} variants={reduced ? still : slot} initial="enter" animate="center" exit="exit">
    {"digit" in part ? <Wheel digit={part.digit} direction={direction} /> : part.text}
  </motion.span>)}</AnimatePresence></span>;
}

const bubbleIn = { opacity: 0, scale: .6, y: 6, filter: `blur(${blur.subtle}px)` };
const bubbleRest = { opacity: 1, scale: 1, y: 0, filter: "blur(0px)", transitionEnd: { filter: "none" } };
const bubbleOut = { opacity: 0, scale: .8, y: 4, filter: `blur(${blur.subtle}px)`, transition: { duration: duration.instant, ease: exitEase } };
const bubbleEnter = { ...spring.snappy, opacity: { duration: duration.fast, ease: enterEase }, filter: { duration: duration.fast, ease: enterEase } };

interface ThumbProps { quiet: boolean; index: number; shown: MotionValue<number>; value: number; text: string; low: number; high: number; ariaLabel?: string; labelledBy?: string; active: boolean; lifted: boolean; bubble: boolean; disabled?: boolean; onKeyDown: (event: KeyboardEvent<HTMLDivElement>) => void; onFocus: (event: FocusEvent<HTMLDivElement>) => void; onBlur: () => void; }

/** The thumb rides a layer as wide as the track, so a percentage transform places it with no measuring, on the server too. */
function Thumb({ quiet, index, shown, value, text, low, high, ariaLabel, labelledBy, active, lifted, bubble, disabled, onKeyDown, onFocus, onBlur }: ThumbProps) {
  const reduced = useReducedMotion();
  const x = useTransform(shown, current => `${current - 100}%`);
  return <motion.div className={styles.thumbLayer} style={{ x, zIndex: active ? 2 : 1 }}>
    <motion.div role="slider" data-thumb="" data-index={index} data-active={lifted || undefined} data-quiet={quiet || undefined} className={styles.thumb} tabIndex={disabled ? -1 : 0}
      aria-label={ariaLabel} aria-labelledby={ariaLabel ? undefined : labelledBy} aria-valuemin={low} aria-valuemax={high} aria-valuenow={value} aria-valuetext={text} aria-orientation="horizontal" aria-disabled={disabled || undefined}
      initial={false} animate={{ scale: lifted ? 1.16 : 1 }} transition={reduced ? { duration: 0 } : spring.snappy} onKeyDown={onKeyDown} onFocus={onFocus} onBlur={onBlur} />
    <motion.span className={styles.bubbleAnchor} style={{ "--at": shown } as MotionStyle}>
      <AnimatePresence>{bubble && <motion.span key="bubble" className={styles.bubble} aria-hidden="true" initial={reduced ? { opacity: 0 } : bubbleIn} animate={bubbleRest} exit={reduced ? { opacity: 0, transition: { duration: duration.instant } } : bubbleOut} transition={reduced ? { duration: duration.instant } : bubbleEnter}>
        <RollingNumber value={value} text={text} />
      </motion.span>}</AnimatePresence>
    </motion.span>
  </motion.div>;
}

/**
 * A value or a range on a track. The thumb follows the pointer 1:1, rubber-bands past its limits, and settles on the step grid with
 * the velocity of the release; pressing the track springs the nearest thumb there. A bubble with rolling digits rides the thumb while
 * it is dragged or keyboard focused. Arrows, Shift+arrows, PageUp, PageDown, Home, and End work as in a native range input.
 */
export function Slider<T extends SliderValue = number>({ label, value, defaultValue, onValueChange, onValueCommit, min = 0, max = 100, step: stepProp = 1, largeStep, marks, minStepsBetweenThumbs = 0, format, showValue = true, thumbLabels, start, end, name, disabled, className }: SliderProps<T>) {
  const reduced = useReducedMotion();
  const labelId = useId();
  const step = stepProp > 0 ? stepProp : 1;
  const span = max - min || 1;
  const isRange = Array.isArray(value ?? defaultValue);
  const toArray = (input: SliderValue | undefined) => input === undefined ? [min, max].slice(0, isRange ? 2 : 1) : Array.isArray(input) ? [input[0], input[1]] : [input];
  const [internal, setInternal] = useState(() => toArray(defaultValue));
  const values = value === undefined ? internal : toArray(value);
  const decimals = Math.max(decimalsOf(step), decimalsOf(min));
  const formatValue = format ?? ((input: number) => input.toLocaleString("en-US", { minimumFractionDigits: decimals, maximumFractionDigits: decimals }));
  const gap = isRange ? minStepsBetweenThumbs * step : 0;
  const pct = (input: number) => ((input - min) / span) * 100;
  const valueAt = (percent: number) => min + (percent / 100) * span;
  const snap = (input: number) => Number((min + Math.round((input - min) / step) * step).toFixed(decimals));
  const lowOf = (index: number, current: number[]) => index === 1 ? current[0]! + gap : min;
  const highOf = (index: number, current: number[]) => index === 0 && isRange ? current[1]! - gap : max;
  const settle = (index: number, input: number, current: number[]) => clamp(snap(clamp(input, min, max)), lowOf(index, current), highOf(index, current));

  const trackRef = useRef<HTMLDivElement>(null);
  const thumbsRef = useRef<HTMLDivElement>(null);
  const latest = useRef(values);
  const goal = useRef(values.map(pct));
  const drag = useRef<Drag | null>(null);
  const pointerFocus = useRef(false);
  const lingerTimer = useRef<number | undefined>(undefined);
  const [dragging, setDragging] = useState<number | null>(null);
  const [keyFocus, setKeyFocus] = useState<number | null>(null);
  const [linger, setLinger] = useState<number | null>(null);
  const [lastActive, setLastActive] = useState(isRange ? 1 : 0);
  const [quiet, setQuiet] = useState<number | null>(null);

  // Each thumb is a committed position plus a catch-up offset: a pressed track springs the offset to zero while the pointer moves the
  // position 1:1, so the thumb glides to the finger and never lags behind it.
  const pos0 = useMotionValue(pct(values[0]!)), pos1 = useMotionValue(pct(values[1] ?? values[0]!));
  const lag0 = useMotionValue(0), lag1 = useMotionValue(0);
  const shown0 = useTransform(() => pos0.get() + lag0.get());
  const shown1 = useTransform(() => pos1.get() + lag1.get());
  const clipPath = useTransform(() => { const from = isRange ? clamp(shown0.get(), 0, 100) : 0; const to = clamp((isRange ? shown1 : shown0).get(), 0, 100); return `inset(0 ${(100 - to).toFixed(3)}% 0 ${from.toFixed(3)}% round 999px)`; });
  const thumbs = [{ pos: pos0, lag: lag0, shown: shown0 }, { pos: pos1, lag: lag1, shown: shown1 }];

  const emit = (next: number[]) => (isRange ? [next[0], next[1]] : next[0]) as T;
  function commit(index: number, next: number) {
    const current = latest.current;
    if (current[index] === next) return;
    const updated = current.map((item, at) => at === index ? next : item);
    latest.current = updated;
    if (value === undefined) setInternal(updated);
    onValueChange?.(emit(updated));
  }

  // Values that arrive from outside a drag (keys, props, a clicked mark) spring the thumb to their place.
  useLayoutEffect(() => {
    latest.current = values;
    values.forEach((item, index) => {
      const target = pct(item), thumb = thumbs[index]!;
      if (drag.current?.index === index || goal.current[index] === target) return;
      goal.current[index] = target;
      const from = thumb.pos.get() + thumb.lag.get();
      thumb.lag.jump(0);
      thumb.pos.jump(from);
      if (reduced) thumb.pos.jump(target); else animate(thumb.pos, target, thumbSpring);
    });
  });
  useEffect(() => () => window.clearTimeout(lingerTimer.current), []);

  const thumbNode = (index: number) => thumbsRef.current?.querySelector<HTMLElement>(`[data-index="${index}"]`);
  /** Focus that follows a pointer keeps the thumb quiet: no ring and no bubble until a key is pressed. */
  function focusFromPointer(index: number) {
    const node = thumbNode(index);
    if (!node || document.activeElement === node) return;
    setQuiet(index);
    pointerFocus.current = true;
    node.focus({ preventScroll: true });
    pointerFocus.current = false;
  }
  const trackWidth = () => trackRef.current?.getBoundingClientRect().width || 1;
  function nearest(at: number, current: number[]) {
    if (!isRange) return 0;
    const low = Math.abs(at - pct(current[0]!)), high = Math.abs(at - pct(current[1]!));
    return low === high ? (at < pct(current[0]!) ? 0 : 1) : low < high ? 0 : 1;
  }
  function holdBubble(index: number) {
    window.clearTimeout(lingerTimer.current);
    setLinger(index);
    lingerTimer.current = window.setTimeout(() => setLinger(null), 700);
  }

  /** Starts tracking one thumb. A press on the track springs the thumb over from where it was; a grabbed thumb keeps its grab offset. */
  function begin(state: Drag, index: number, at: number, press: boolean) {
    const thumb = thumbs[index]!;
    const from = thumb.pos.get() + thumb.lag.get();
    state.index = index;
    state.grab = press ? 0 : at - from;
    thumb.lag.jump(0);
    thumb.pos.jump(from);
    setDragging(index);
    setLastActive(index);
    window.clearTimeout(lingerTimer.current);
    setLinger(null);
    focusFromPointer(index);
    if (press) follow(state, at, 0, true);
  }
  /** Places the dragged thumb under the pointer, with resistance past its limits, and commits the stepped value beneath it. */
  function follow(state: Drag, at: number, time: number, press = false) {
    const index = state.index;
    if (index === null) return;
    const thumb = thumbs[index]!, current = latest.current, width = trackWidth();
    const raw = at - state.grab;
    const low = pct(lowOf(index, current)), high = pct(highOf(index, current));
    const edge = clamp(raw, low, high);
    const placed = reduced ? edge : edge + (rubber(((raw - edge) / 100) * width) / width) * 100;
    if (press && !reduced) {
      const from = thumb.pos.get();
      thumb.pos.jump(placed);
      thumb.lag.jump(from - placed);
      animate(thumb.lag, 0, thumbSpring);
    } else thumb.pos.set(placed);
    state.raw = raw;
    state.samples.push({ t: time, p: placed });
    if (state.samples.length > 6) state.samples.shift();
    commit(index, settle(index, valueAt(raw), current));
  }

  function onPointerDown(event: PointerEvent<HTMLDivElement>) {
    if (disabled || event.button !== 0 || !event.isPrimary) return;
    const rect = trackRef.current?.getBoundingClientRect();
    if (!rect) return;
    const at = ((event.clientX - rect.left) / (rect.width || 1)) * 100;
    const grabbed = (event.target as HTMLElement).closest<HTMLElement>("[data-thumb]");
    const current = latest.current;
    event.currentTarget.setPointerCapture(event.pointerId);
    const state: Drag = { pointer: event.pointerId, index: null, grab: 0, x: event.clientX, raw: at, samples: [] };
    drag.current = state;
    // Stacked range thumbs wait for the first movement to decide which one the pointer means.
    if (grabbed && isRange && current[0] === current[1]) { focusFromPointer(Number(grabbed.dataset.index)); return; }
    begin(state, grabbed ? Number(grabbed.dataset.index) : nearest(at, current), at, !grabbed);
    state.samples = [{ t: event.timeStamp, p: thumbs[state.index ?? 0]!.pos.get() }];
  }
  function onPointerMove(event: PointerEvent<HTMLDivElement>) {
    const state = drag.current;
    if (!state || state.pointer !== event.pointerId) return;
    const rect = trackRef.current?.getBoundingClientRect();
    if (!rect) return;
    const at = ((event.clientX - rect.left) / (rect.width || 1)) * 100;
    if (state.index === null) {
      const dx = event.clientX - state.x;
      if (Math.abs(dx) < 2) return;
      begin(state, dx < 0 ? 0 : 1, at - (dx / (rect.width || 1)) * 100, false);
    }
    follow(state, at, event.timeStamp);
  }
  function onPointerEnd(event: PointerEvent<HTMLDivElement>) {
    const state = drag.current;
    if (!state || state.pointer !== event.pointerId) return;
    drag.current = null;
    const index = state.index;
    if (index === null) return;
    const thumb = thumbs[index]!, current = latest.current, width = trackWidth();
    // Velocity from the last few frames, in percent per second; a pointer that paused before letting go carries none.
    const samples = state.samples, first = samples[0], last = samples[samples.length - 1];
    const elapsed = last && first ? (last.t - first.t) / 1000 : 0;
    // A release during the catch-up spring keeps that spring's speed too.
    const velocity = (last && first && elapsed > .008 && event.timeStamp - last.t < 60 ? (last.p - first.p) / elapsed : 0) + thumb.lag.getVelocity();
    // On a coarse grid the release momentum may carry the thumb one step further, never more.
    const stepPct = (step / span) * 100;
    const carry = event.type === "pointerup" && (stepPct / 100) * width >= SNAP_PX ? clamp(project(velocity), -stepPct, stepPct) : 0;
    const next = settle(index, valueAt(state.raw + carry), current);
    const from = thumb.pos.get() + thumb.lag.get(), target = pct(next);
    thumb.lag.jump(0);
    thumb.pos.jump(from);
    goal.current[index] = target;
    if (reduced) thumb.pos.jump(target); else animate(thumb.pos, target, { ...thumbSpring, velocity });
    commit(index, next);
    setDragging(null);
    holdBubble(index);
    onValueCommit?.(emit(latest.current));
  }

  function onKeyDown(index: number, event: KeyboardEvent<HTMLDivElement>) {
    if (disabled) return;
    const current = latest.current, now = current[index]!;
    const large = largeStep ?? Math.max(step, snap(min + span / 10) - min);
    const moves: Record<string, number> = { ArrowRight: step, ArrowUp: step, ArrowLeft: -step, ArrowDown: -step, PageUp: large, PageDown: -large };
    let wanted: number;
    if (event.key === "Home") wanted = lowOf(index, current);
    else if (event.key === "End") wanted = highOf(index, current);
    else if (event.key in moves) wanted = now + (event.shiftKey && /^Arrow/.test(event.key) ? Math.sign(moves[event.key]!) * large : moves[event.key]!);
    else return;
    event.preventDefault();
    setKeyFocus(index);
    setQuiet(null);
    setLastActive(index);
    const next = settle(index, wanted, current);
    // At a limit the thumb strains toward the press and springs home, so the key still answers.
    if (next === now) {
      const toward = Math.sign(wanted - now);
      if (toward && !reduced) animate(thumbs[index]!.pos, goal.current[index]!, { ...kickSpring, velocity: (toward * BUMP_PX / trackWidth()) * 100 });
      return;
    }
    commit(index, next);
    onValueCommit?.(emit(latest.current));
  }

  function jumpTo(target: number) {
    if (disabled) return;
    const current = latest.current, index = nearest(pct(target), current);
    const next = settle(index, target, current);
    setLastActive(index);
    focusFromPointer(index);
    if (next === current[index]) return;
    commit(index, next);
    onValueCommit?.(emit(latest.current));
  }

  const markList = (marks ?? []).map(mark => typeof mark === "number" ? { value: mark } as SliderMark : mark).filter(mark => mark.value >= min && mark.value <= max);
  const ticks = markList.filter(mark => mark.value > min && mark.value < max);
  const labelled = markList.filter(mark => mark.label);
  const inRange = (mark: number) => isRange ? mark >= values[0]! && mark <= values[1]! : mark <= values[0]!;
  const names = thumbLabels ?? [`${label}, minimum`, `${label}, maximum`];

  return <div className={[styles.root, className].filter(Boolean).join(" ")} data-disabled={disabled || undefined} data-dragging={dragging !== null || undefined} data-marks={labelled.length > 0 || undefined}>
    <div className={styles.header}>
      <span id={labelId} className={styles.label}>{label}</span>
      {showValue && <span className={styles.readout} aria-hidden="true">
        <RollingNumber value={values[0]!} text={formatValue(values[0]!)} />
        {isRange && <><span className={styles.dash}>–</span><RollingNumber value={values[1]!} text={formatValue(values[1]!)} /></>}
      </span>}
    </div>
    <div className={styles.body}>
      {start && <div className={styles.start}>{start}</div>}
      <div className={styles.control} onPointerDown={onPointerDown} onPointerMove={onPointerMove} onPointerUp={onPointerEnd} onPointerCancel={onPointerEnd} onLostPointerCapture={onPointerEnd} onMouseDown={event => event.preventDefault()}>
        <div ref={trackRef} className={styles.track}>
          {ticks.map(mark => <span key={mark.value} className={styles.tick} style={{ left: `${pct(mark.value)}%` }} />)}
          <motion.div className={styles.fill} style={{ clipPath }}>{ticks.map(mark => <span key={mark.value} className={styles.tick} style={{ left: `${pct(mark.value)}%` }} />)}</motion.div>
        </div>
        <div ref={thumbsRef} className={styles.thumbs}>
          {values.map((item, index) => <Thumb key={index} quiet={quiet === index} index={index} shown={thumbs[index]!.shown} value={item} text={formatValue(item)} low={lowOf(index, values)} high={highOf(index, values)}
            ariaLabel={isRange ? names[index] : undefined} labelledBy={labelId} active={lastActive === index} lifted={dragging === index} bubble={dragging === index || keyFocus === index || linger === index} disabled={disabled}
            onKeyDown={event => onKeyDown(index, event)} onFocus={event => { if (!pointerFocus.current && event.currentTarget.matches(":focus-visible")) setKeyFocus(index); }} onBlur={() => { setKeyFocus(current => current === index ? null : current); setQuiet(current => current === index ? null : current); }} />)}
        </div>
      </div>
      {end && <div className={styles.end}>{end}</div>}
      {labelled.length > 0 && <div className={styles.marks} aria-hidden="true">
        {labelled.map(mark => <span key={mark.value} className={styles.markLabel} data-on={inRange(mark.value) || undefined} data-edge={mark.value === min ? "start" : mark.value === max ? "end" : undefined} style={{ left: `${pct(mark.value)}%` }} onClick={() => jumpTo(mark.value)}>{mark.label}</span>)}
      </div>}
    </div>
    {name && values.map((item, index) => <input key={index} type="hidden" name={name} value={item} disabled={disabled} />)}
  </div>;
}

export default Slider;
