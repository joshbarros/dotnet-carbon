import { test, expect } from '@playwright/test';
import { ADMIN, adminToken, signIn } from './support';

test('add a batch, buy credits, see the certificate, reverse it', async ({ page }) => {
  const run = Date.now().toString(36).toUpperCase();
  const code = `E2E-${run}`;
  const reference = `ORG-${run}`;

  await signIn(page, ADMIN);
  await page.getByLabel('Batch code').fill(code);
  await page.getByLabel('Tonnes issued').fill('8');
  await page.getByRole('button', { name: 'Add batch' }).click();

  const remaining = page.getByTestId('selected-remaining');
  await expect(page.getByRole('heading', { name: `Buy credits from ${code}` })).toBeVisible();
  await expect(remaining).toHaveText('8');

  await page.getByLabel('Beneficiary reference').fill(reference);
  await page.getByLabel('Tonnes', { exact: true }).fill('3');
  await page.getByRole('button', { name: 'Buy credits' }).click();

  const row = page.locator(`tr[data-reference="${reference}"]`);
  await expect(row.getByTestId('status')).toHaveText('Allocated', { timeout: 20_000 });
  await expect(row.getByTestId('certificate')).toHaveText(/CC-[0-9A-F]{12}/);
  await expect(remaining).toHaveText('5');

  // Nothing is kept in the browser: a fresh load reads the same state from the API.
  // Nothing sensitive is stored either: the refresh cookie is HttpOnly, so a reload signs back in through it.
  expect(await page.evaluate(() => JSON.stringify({ ...localStorage, ...sessionStorage }))).toBe(
    '{}',
  );
  await page.reload();
  await expect(page.getByTestId('whoami')).toContainText(ADMIN.email);
  await page.getByRole('button', { name: new RegExp(`^${code}`) }).click();
  const reloaded = page.locator(`tr[data-reference="${reference}"]`);
  await expect(reloaded.getByTestId('status')).toHaveText('Allocated');
  await expect(page.getByTestId('selected-remaining')).toHaveText('5');

  await reloaded.getByRole('button', { name: 'Reverse' }).click();
  await expect(reloaded.getByTestId('status')).toHaveText('Reversed');
  await expect(page.getByTestId('selected-remaining')).toHaveText('8');
  // The issued certificate is not rolled back.
  await expect(reloaded.getByTestId('certificate')).toHaveText(/CC-[0-9A-F]{12}/);
  const headers = { Authorization: `Bearer ${await adminToken(page.request)}` };
  const purchases = await page.request.get(`/api/purchases?batchCode=${code}`, { headers });
  const [purchase] = await purchases.json();
  const certificate = await page.request.get(`/api/certificates/${purchase.id}`, { headers });
  expect((await certificate.json()).status).toBe('Issued');
});
