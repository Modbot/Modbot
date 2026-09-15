import Link from 'next/link';

export default function NotFound() {
  return (
    <main className="flex flex-1 flex-col items-center justify-center gap-3 p-8 text-center">
      <h1 className="font-display text-2xl">Page not found</h1>
      <Link href="/" className="text-fd-primary underline underline-offset-2">
        Go to the introduction
      </Link>
    </main>
  );
}
