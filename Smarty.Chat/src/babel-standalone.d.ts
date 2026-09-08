/**
 * @babel/standalone ships no types. Only `transform` is used, and only for JSX, so a one-line declaration is
 * honest — pulling in a full type package for one function would be more to keep current than it is worth.
 */
declare module '@babel/standalone' {
  export function transform(code: string, options: Record<string, unknown>): { code: string | null }
}
