import AxeBuilder from '@axe-core/playwright';
import { Page, expect, test } from '@playwright/test';
import { signIn } from './helpers';

async function audit(page: Page, name: string): Promise<void> {
  for (const colorScheme of ['light', 'dark'] as const) {
    await page.emulateMedia({ colorScheme });
    const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze();
    expect(results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical').map((v) => `${name} (${colorScheme}): ${v.id}`)).toEqual([]);
  }

  await page.emulateMedia({ colorScheme: null });
}

/**
 * REQ-AI-006 (ADR 0024): a permission administrator sees the operator's models (none are configured in the acceptance
 * stack, so the builder uses its built-in rules), and sets the workspace's daily AI budget.
 */
test('@REQ-AI-006 an administrator sets the AI model choice and daily budget for the workspace', async ({ page }) => {
  await signIn(page, 'morgan@globex.example', 'Globex');
  await page.getByRole('link', { name: 'AI assistant' }).click();
  await page.waitForURL(/\/admin\/ai$/);
  await expect(page.getByText('No model is configured on this platform')).toBeVisible();
  await expect(page.locator('option', { hasText: 'GPT-6.1 Sol' })).toHaveJSProperty('disabled', true);
  await expect(page.locator('option', { hasText: 'GPT-6.1 Sol' })).toContainText('not configured');
  await audit(page, 'ai settings');

  await page.getByLabel(/Daily budget/).fill('12.5');
  await page.getByLabel(/External assistants/).selectOption('non_production');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByText('AI settings saved.')).toBeVisible();
  await page.reload();
  await expect(page.getByText('of $12.50')).toBeVisible();
  await expect(page.getByLabel(/External assistants/)).toHaveValue('non_production');
});

/** REQ-AI-007 (ADR 0025): people see the external assistants connected to the workspace and can end them. */
test('@REQ-AI-007 the connected assistants page lists connections and has no accessibility violations', async ({ page }) => {
  await signIn(page, 'morgan@globex.example', 'Globex');
  await page.getByRole('link', { name: 'Connected assistants' }).click();
  await page.waitForURL(/\/assistants$/);
  await expect(page.getByText('No assistants are connected.')).toBeVisible();
  await page.getByLabel("Everyone's").check();
  await expect(page.getByText('No assistants are connected.')).toBeVisible();
  await audit(page, 'connected assistants');
});
