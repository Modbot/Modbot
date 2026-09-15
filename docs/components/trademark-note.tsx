import Link from 'next/link';

/**
 * The line at the foot of every page: the landing page footer's wording
 * (src/Modbot.Landing/Web/src/components/Site.tsx), pointing to the Legal page for the other
 * marks these docs name.
 */
export function TrademarkNote() {
  return (
    <footer className="mt-12 border-t pt-4 text-xs leading-relaxed text-fd-muted-foreground">
      <p>
        Modbot is not endorsed by or affiliated with VRChat Inc. or Discord Inc. VRChat is a trademark of VRChat
        Inc. Discord is a trademark of Discord Inc. SteamVR is a trademark of Valve Corporation. Windows is a
        trademark of Microsoft Corporation. Docker is a trademark of Docker, Inc. PostgreSQL is a trademark of the
        PostgreSQL Community Association of Canada. Railway is a trademark of Railway Corp. Other names are
        trademarks of their respective owners; see{' '}
        <Link href="/legal/" className="underline underline-offset-2">
          Legal
        </Link>
        . Modbot and its logo are trademarks of the Modbot project. Open source under AGPL-3.0.
      </p>
    </footer>
  );
}
