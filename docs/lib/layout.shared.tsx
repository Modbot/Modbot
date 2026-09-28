import type { BaseLayoutProps } from 'fumadocs-ui/layouts/shared';
import { Bug } from 'lucide-react';
import Image from 'next/image';
import { issuesLabel, issuesUrl, landingUrl } from './shared';

export function baseOptions(): BaseLayoutProps {
  return {
    nav: {
      title: (
        <span className="font-display flex items-center gap-2 text-[0.9375rem]">
          {/* The head-only mark, as every Modbot surface shows it (brand design 2026-09-16 §2). */}
          <Image src="/icon-512.png" alt="" width={24} height={24} unoptimized className="size-6 shrink-0" />
          Modbot Docs
        </span>
      ),
      url: '/',
    },
    links: [
      {
        text: 'modbot.co',
        url: landingUrl,
        external: true,
      },
      {
        icon: <Bug />,
        text: issuesLabel,
        url: issuesUrl,
        external: true,
      },
    ],
  };
}
