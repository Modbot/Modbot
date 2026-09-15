import type { BaseLayoutProps } from 'fumadocs-ui/layouts/shared';
import { landingUrl } from './shared';

export function baseOptions(): BaseLayoutProps {
  return {
    nav: {
      title: (
        <span className="flex items-center gap-2 font-semibold">
          <span
            aria-hidden
            className="inline-grid size-6 place-items-center rounded-md bg-fd-primary text-xs font-bold text-fd-primary-foreground"
          >
            M
          </span>
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
    ],
  };
}
