import type { Metadata, Viewport } from 'next';
import type { ReactNode } from 'react';
import { Provider } from '@/components/provider';
import { siteName, siteUrl } from '@/lib/shared';
import './global.css';

export const metadata: Metadata = {
  metadataBase: new URL(siteUrl),
  title: {
    default: siteName,
    template: `%s | ${siteName}`,
  },
  description: 'How to host, set up and use Modbot, and its API.',
  icons: {
    icon: [
      { url: '/favicon.svg', type: 'image/svg+xml' },
      { url: '/icon-192.png', type: 'image/png', sizes: '192x192' },
    ],
    apple: '/apple-touch-icon.png',
  },
  manifest: '/manifest.webmanifest',
  openGraph: {
    siteName,
    title: siteName,
    description: 'How to host, set up and use Modbot, and its API.',
    images: [{ url: '/og.png', width: 1200, height: 630 }],
  },
};

export const viewport: Viewport = {
  themeColor: [
    { media: '(prefers-color-scheme: light)', color: '#f7f6fb' },
    { media: '(prefers-color-scheme: dark)', color: '#0f0e15' },
  ],
};

export default function Layout({ children }: { children: ReactNode }) {
  return (
    <html lang="en" suppressHydrationWarning>
      <body className="flex min-h-screen flex-col">
        <Provider>{children}</Provider>
      </body>
    </html>
  );
}
