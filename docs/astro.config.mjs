import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import { site, base } from './site.config.mjs';

export default defineConfig({
  site,
  base,
  trailingSlash: 'always',
  integrations: [
    starlight({
      title: 'GitHub for Command Palette',
      description: 'Check your GitHub work from Command Palette. Installation, sign-in, feature guides, and troubleshooting.',
      logo: {
        src: '../GitHubExtension/Assets/GHCmdPalMark.svg',
        replacesTitle: false,
      },
      favicon: '/favicon.svg',
      social: [
        { icon: 'github', label: 'GitHub', href: 'https://github.com/baldbeardedbuilder/CmdPalGitHubExtension' },
      ],
      editLink: {
        baseUrl: 'https://github.com/baldbeardedbuilder/CmdPalGitHubExtension/edit/main/docs/',
      },
      customCss: ['./src/styles/custom.css'],
      sidebar: [
        { label: 'Home', slug: 'index' },
        {
          label: 'Getting started',
          items: [
            { label: 'Install the extension', slug: 'getting-started/installation' },
            { label: 'Sign in', slug: 'getting-started/sign-in' },
          ],
        },
        {
          label: 'Feature guides',
          items: [
            { label: 'Notifications', slug: 'guides/notifications' },
            { label: 'Repositories', slug: 'guides/repositories' },
            { label: 'Issues', slug: 'guides/issues' },
            { label: 'Pull requests', slug: 'guides/pull-requests' },
            { label: 'Copilot agent tasks', slug: 'guides/agents' },
            { label: 'Codespaces', slug: 'guides/codespaces' },
            { label: 'GitHub Actions', slug: 'guides/actions' },
          ],
        },
        {
          label: 'Reference',
          items: [
            { label: 'Filtering and search', slug: 'reference/filtering' },
            { label: 'Confirmations and pending requests', slug: 'reference/request-safety' },
            { label: 'Troubleshooting', slug: 'reference/troubleshooting' },
            { label: 'Privacy', slug: 'reference/privacy' },
          ],
        },
      ],
    }),
  ],
});
