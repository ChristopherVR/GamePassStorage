import { defineConfig } from 'vitepress'

// VitePress config for the GamePassStorage docs site.
// Deployed to GitHub Pages at https://christophervr.github.io/GamePassStorage/ by
// .github/workflows/pages.yml, so `base` must be the repository name.
export default defineConfig({
  title: 'GamePassStorage',
  description:
    'A game-agnostic .NET library and the wgs tool for reading and safely writing Xbox Connected Storage save folders used by Game Pass PC titles.',
  base: '/GamePassStorage/',
  lang: 'en-US',
  cleanUrls: true,
  lastUpdated: true,

  // Prose uses literal angle brackets (<store>, container.<N>), so raw HTML passthrough stays off.
  markdown: { html: false },

  head: [
    ['link', { rel: 'icon', type: 'image/svg+xml', href: '/GamePassStorage/logo.svg' }],
    ['meta', { name: 'og:title', content: 'GamePassStorage' }],
    ['meta', { name: 'og:description', content: 'Read and safely write Xbox Connected Storage (wgs) save folders from .NET.' }],
  ],

  themeConfig: {
    logo: '/logo.svg',

    nav: [
      { text: 'Guide', link: '/guide/', activeMatch: '/guide/' },
      { text: 'CLI', link: '/cli/', activeMatch: '/cli/' },
      { text: 'API', link: '/api/', activeMatch: '/api/' },
      { text: 'Format', link: '/wgs-format' },
      {
        text: 'Project',
        items: [
          { text: 'Supported titles', link: '/supported-titles' },
          { text: 'Contributing', link: '/contributing' },
          { text: 'Releasing', link: '/releasing' },
          { text: 'NuGet: GamePassStorage', link: 'https://www.nuget.org/packages/GamePassStorage' },
          { text: 'NuGet: GamePassStorage.Tool', link: 'https://www.nuget.org/packages/GamePassStorage.Tool' },
        ],
      },
    ],

    sidebar: {
      '/guide/': [
        {
          text: 'Guide',
          items: [
            { text: 'Introduction', link: '/guide/' },
            { text: 'Getting started', link: '/guide/getting-started' },
            { text: 'Safety model', link: '/guide/safety' },
            { text: 'Writing a game adapter', link: '/guide/adapter' },
            { text: 'Testing with an in-memory filesystem', link: '/guide/testing' },
            { text: 'Troubleshooting', link: '/guide/troubleshooting' },
          ],
        },
        {
          text: 'More',
          items: [
            { text: 'CLI reference', link: '/cli/' },
            { text: 'API reference', link: '/api/' },
            { text: 'Format reference', link: '/wgs-format' },
            { text: 'Supported titles', link: '/supported-titles' },
          ],
        },
      ],
      '/cli/': [
        {
          text: 'The wgs tool',
          items: [
            { text: 'CLI reference', link: '/cli/' },
          ],
        },
        {
          text: 'Guide',
          items: [
            { text: 'Safety model', link: '/guide/safety' },
            { text: 'Troubleshooting', link: '/guide/troubleshooting' },
          ],
        },
      ],
      '/api/': [
        {
          text: 'API reference',
          items: [
            { text: 'Overview', link: '/api/' },
            { text: 'WgsStore', link: '/api/wgs-store' },
            { text: 'Containers and state', link: '/api/containers' },
            { text: 'Results and status codes', link: '/api/results' },
            { text: 'Write gates and assessments', link: '/api/write-gates' },
            { text: 'Injected services', link: '/api/services' },
            { text: 'Snapshots', link: '/api/snapshots' },
          ],
        },
      ],
      '/': [
        {
          text: 'Reference and project',
          items: [
            { text: 'Format reference', link: '/wgs-format' },
            { text: 'Supported titles', link: '/supported-titles' },
            { text: 'Contributing', link: '/contributing' },
            { text: 'Releasing', link: '/releasing' },
          ],
        },
      ],
    },

    search: { provider: 'local' },

    socialLinks: [
      { icon: 'github', link: 'https://github.com/ChristopherVR/GamePassStorage' },
    ],

    editLink: {
      pattern: 'https://github.com/ChristopherVR/GamePassStorage/edit/main/docs/:path',
      text: 'Edit this page on GitHub',
    },

    footer: {
      message: 'Released under the Apache-2.0 license. Not affiliated with Microsoft.',
      copyright: 'GamePassStorage',
    },
  },
})
