// Opening a picture in the browser and making the cropped PNG or JPEG from it (calendar design §15.3).
// Whatever the browser can draw goes in -- WebP, AVIF, GIF (its first frame), BMP, HEIC where the
// browser reads it -- and what comes out is a PNG, or a JPEG when the PNG would be too large. The
// server never turns one kind of picture into another: it has no image library, and this way none
// of the work or the bytes of the conversion are on it.
import { croppedSize, type CropBox } from './eventPicture.ts'
import { iconLayout } from './eventPlatforms.ts'
import { PLATFORM_ICONS } from './platformIcons.ts'

/** A picture the browser has drawn, and the address it was drawn from (revoke with `closePicture`). */
export type OpenPicture = { image: HTMLImageElement; url: string; width: number; height: number }

export const CANNOT_OPEN = 'Could not open the picture.'

/** Draws `file` in the browser. Throws `CANNOT_OPEN` when the browser cannot read it. */
export async function openPicture(file: Blob): Promise<OpenPicture> {
  const url = URL.createObjectURL(file)
  const image = new Image()
  image.src = url

  try {
    await image.decode()
  } catch {
    URL.revokeObjectURL(url)
    throw new Error(CANNOT_OPEN)
  }

  if (image.naturalWidth === 0 || image.naturalHeight === 0) {
    URL.revokeObjectURL(url)
    throw new Error(CANNOT_OPEN)
  }

  return { image, url, width: image.naturalWidth, height: image.naturalHeight }
}

export function closePicture(picture: OpenPicture | null | undefined): void {
  if (picture) URL.revokeObjectURL(picture.url)
}

/** Draws the crop of `picture` onto `canvas`, at the canvas's own size. */
export function drawCrop(canvas: HTMLCanvasElement, picture: OpenPicture, box: CropBox): void {
  const context = canvas.getContext('2d')
  if (!context) return

  context.imageSmoothingQuality = 'high'
  context.clearRect(0, 0, canvas.width, canvas.height)
  context.drawImage(picture.image, box.x, box.y, box.width, box.height, 0, 0, canvas.width, canvas.height)
}

/**
 * Draws the platform icons in the top right corner of `canvas`, the way the event form's preview shows
 * them: each in its own colour in a ring, on a dark disc so it reads on any picture. Platforms with
 * no icon are left out.
 */
export function drawPlatformIcons(canvas: HTMLCanvasElement, platforms: readonly string[]): void {
  const context = canvas.getContext('2d')
  const icons = platforms.flatMap((p) => (PLATFORM_ICONS[p] ? [PLATFORM_ICONS[p]] : []))
  if (!context || icons.length === 0) return

  const { diameter, gap, x, y } = iconLayout(canvas.width, icons.length)

  icons.forEach((icon, index) => {
    const left = x + index * (diameter + gap)
    const ring = diameter * 0.08
    const [, , viewWidth, viewHeight] = icon.viewBox.split(' ').map(Number)
    const scale = (diameter * 0.5) / Math.max(viewWidth, viewHeight)

    context.beginPath()
    context.arc(left + diameter / 2, y + diameter / 2, diameter / 2 - ring / 2, 0, Math.PI * 2)
    context.fillStyle = 'rgb(0 0 0 / 0.55)'
    context.fill()
    context.lineWidth = ring
    context.strokeStyle = icon.colour
    context.stroke()

    context.save()
    context.translate(left + (diameter - viewWidth * scale) / 2, y + (diameter - viewHeight * scale) / 2)
    context.scale(scale, scale)
    context.fillStyle = icon.colour
    context.fill(new Path2D(icon.d))
    context.restore()
  })
}

function toBlob(canvas: HTMLCanvasElement, type: string, quality?: number): Promise<Blob | null> {
  return new Promise((resolve) => canvas.toBlob(resolve, type, quality))
}

/**
 * The crop as a picture file: a PNG, or a JPEG when the PNG would be larger than `maxBytes`, with
 * the `platforms` icons drawn in its corner. Throws a plain sentence when neither fits.
 */
export async function cropToFile(
  picture: OpenPicture,
  box: CropBox,
  aspect: number,
  maxBytes: number,
  platforms: readonly string[] = [],
): Promise<Blob> {
  const size = croppedSize(box, aspect)
  const canvas = document.createElement('canvas')
  canvas.width = size.width
  canvas.height = size.height
  drawCrop(canvas, picture, box)
  drawPlatformIcons(canvas, platforms)

  const png = await toBlob(canvas, 'image/png')
  if (png && png.type === 'image/png' && png.size <= maxBytes) return png

  const jpeg = await toBlob(canvas, 'image/jpeg', 0.9)
  if (jpeg && jpeg.type === 'image/jpeg' && jpeg.size <= maxBytes) return jpeg

  throw new Error(`The picture is larger than ${maxBytes / (1024 * 1024)} MB.`)
}

/** The longest side of the picture Bluesky is sent: it shows in full in the feed, and 2000 pixels is a sharp one. */
const BLUESKY_PICTURE_SIDE = 2000

/**
 * A small JPEG copy of a kept picture for Bluesky's post (posts design §4.2c): at most
 * `maxBytes` (Modbot sends 1,000,000), at most 2000 pixels on its longer side, made smaller and
 * plainer step by step until it fits. `side` replaces the 2000 pixels for a picture that needs fewer.
 * Made here because the server has no image library. Throws a plain sentence when even the smallest
 * step does not fit.
 */
export async function smallJpeg(file: Blob, maxBytes: number, side = BLUESKY_PICTURE_SIDE): Promise<Blob> {
  const picture = await openPicture(file)

  try {
    const longer = Math.max(picture.width, picture.height)
    const steps: [number, number][] = [
      [1, 0.85],
      [1, 0.7],
      [0.75, 0.7],
      [0.5, 0.7],
      [0.35, 0.6],
    ]

    for (const [scale, quality] of steps) {
      const factor = Math.min(1, side / longer) * scale
      const canvas = document.createElement('canvas')
      canvas.width = Math.max(1, Math.round(picture.width * factor))
      canvas.height = Math.max(1, Math.round(picture.height * factor))
      const context = canvas.getContext('2d')
      if (!context) break

      // A JPEG has no transparency: what a PNG leaves see-through would come out black on the card,
      // so it is drawn on white.
      context.fillStyle = '#ffffff'
      context.fillRect(0, 0, canvas.width, canvas.height)
      context.imageSmoothingQuality = 'high'
      context.drawImage(picture.image, 0, 0, picture.width, picture.height, 0, 0, canvas.width, canvas.height)

      const jpeg = await toBlob(canvas, 'image/jpeg', quality)
      if (jpeg && jpeg.type === 'image/jpeg' && jpeg.size <= maxBytes) return jpeg
    }
  } finally {
    closePicture(picture)
  }

  throw new Error('Could not make the picture small enough for Bluesky.')
}
