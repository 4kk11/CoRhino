import { defineConfig, type Plugin } from 'vite'
import { viteSingleFile } from 'vite-plugin-singlefile'

function classicAtBodyEnd(): Plugin {
  return {
    name: 'classic-at-body-end',
    transformIndexHtml(html) {
      html = html
        .replace(/ type="module"/g, '')
        .replace(/ crossorigin/g, '')
      // Move the (huge) inline <script>...</script> to just before </body>
      // so #terminal exists in the DOM by the time the bundle runs.
      const m = html.match(/<script[^>]*>[\s\S]*?<\/script>/g)
      if (m && m.length > 0) {
        const main = m.find((t) => t.length > 2000) ?? m[m.length - 1]
        html = html.replace(main, '')
        html = html.replace('</body>', `  ${main}\n  </body>`)
      }
      return html
    },
  }
}

export default defineConfig({
  plugins: [viteSingleFile(), classicAtBodyEnd()],
  build: {
    outDir: '../src/bin/Debug/net8.0/web',
    emptyOutDir: true,
    rollupOptions: {
      output: {
        format: 'iife',
        name: 'RhinoClaudePanelUI',
        inlineDynamicImports: true,
      },
    },
  },
})
