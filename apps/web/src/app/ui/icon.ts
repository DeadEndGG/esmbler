import { ChangeDetectionStrategy, Component, ElementRef, effect, inject, input } from '@angular/core';
import {
  Activity,
  ArrowDown,
  ArrowLeft,
  ArrowUp,
  ArrowUpRight,
  Building2,
  Check,
  ChevronRight,
  ChevronsUpDown,
  CircleAlert,
  CircleCheck,
  Clock,
  Command,
  CornerDownLeft,
  FileText,
  GripVertical,
  History,
  Inbox,
  Info,
  Keyboard,
  Layers,
  LogOut,
  MessageSquare,
  Monitor,
  Moon,
  Package,
  Plus,
  RotateCcw,
  Search,
  Send,
  ShieldCheck,
  SlidersHorizontal,
  Sparkles,
  Sun,
  Ticket,
  Trash2,
  TriangleAlert,
  Undo2,
  Users,
  X,
  Zap,
  createElement,
  type IconNode,
} from 'lucide';

/** The approved icon set (design lint DL-010): Lucide, ISC license. Add an icon here before using it. */
const ICONS = {
  activity: Activity,
  'arrow-down': ArrowDown,
  'arrow-left': ArrowLeft,
  'arrow-up': ArrowUp,
  'arrow-up-right': ArrowUpRight,
  building: Building2,
  check: Check,
  'chevron-right': ChevronRight,
  'chevrons-up-down': ChevronsUpDown,
  'circle-alert': CircleAlert,
  'circle-check': CircleCheck,
  clock: Clock,
  command: Command,
  enter: CornerDownLeft,
  file: FileText,
  grip: GripVertical,
  history: History,
  inbox: Inbox,
  info: Info,
  keyboard: Keyboard,
  layers: Layers,
  'log-out': LogOut,
  message: MessageSquare,
  monitor: Monitor,
  moon: Moon,
  package: Package,
  plus: Plus,
  reset: RotateCcw,
  search: Search,
  send: Send,
  shield: ShieldCheck,
  sliders: SlidersHorizontal,
  sparkles: Sparkles,
  sun: Sun,
  ticket: Ticket,
  trash: Trash2,
  warning: TriangleAlert,
  undo: Undo2,
  users: Users,
  x: X,
  zap: Zap,
} satisfies Record<string, IconNode>;

export type IconName = keyof typeof ICONS;

/**
 * An icon from the approved set. Icons are decorative (`aria-hidden`): the control they sit in carries the
 * accessible name. Stroke color follows `currentColor`, so icons take the token color of their text.
 */
@Component({
  selector: 'esm-icon',
  template: '',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { 'aria-hidden': 'true', class: 'esm-icon', '[style.--esm-icon-size.px]': 'size()' },
  styles: `
    :host { display: inline-flex; flex: none; width: var(--esm-icon-size); height: var(--esm-icon-size); }
    :host ::ng-deep svg { width: 100%; height: 100%; }
  `,
})
export class Icon {
  readonly name = input.required<IconName>();
  readonly size = input(16);
  readonly stroke = input(2);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  constructor() {
    effect(() => {
      const svg = createElement(ICONS[this.name()] ?? ICONS.info, { 'stroke-width': this.stroke(), width: this.size(), height: this.size(), focusable: 'false' });
      this.host.nativeElement.replaceChildren(svg);
    });
  }
}
