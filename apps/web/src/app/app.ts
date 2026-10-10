import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { ThemeService } from './core/theme';
import { ToastHost } from './ui/toast-host';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, ToastHost],
  template: `<router-outlet /><esm-toast-host />`,
})
export class App {
  // Applies the saved theme before first paint of routed content.
  protected readonly theme = inject(ThemeService);
}
