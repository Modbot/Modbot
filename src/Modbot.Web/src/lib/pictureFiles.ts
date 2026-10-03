// Opening a picture in the browser and making the cropped PNG or JPEG from it (calendar design §15.3).
// Whatever the browser can draw goes in -- WebP, AVIF, GIF (its first frame), BMP, HEIC where the
// browser reads it -- and what comes out is a PNG, or a JPEG when the PNG would be too large. The
// server never turns one kind of picture into another: it has no image library, and this way none
// of the work or the bytes of the conversion are on it.
import { croppedSize, type CropBox } from './eventPicture.ts'

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

function toBlob(canvas: HTMLCanvasElement, type: string, quality?: number): Promise<Blob | null> {
  return new Promise((resolve) => canvas.toBlob(resolve, type, quality))
}

/**
 * The crop as a picture file: a PNG, or a JPEG when the PNG would be larger than `maxBytes`.
 * Throws a plain sentence when neither fits.
 */
export async function cropToFile(picture: OpenPicture, box: CropBox, aspect: number, maxBytes: number): Promise<Blob> {
  const size = croppedSize(box, aspect)
  const canvas = document.createElement('canvas')
  canvas.width = size.width
  canvas.height = size.height
  drawCrop(canvas, picture, box)

  const png = await toBlob(canvas, 'image/png')
  if (png && png.type === 'image/png' && png.size <= maxBytes) return png

  const jpeg = await toBlob(canvas, 'image/jpeg', 0.9)
  if (jpeg && jpeg.type === 'image/jpeg' && jpeg.size <= maxBytes) return jpeg

  throw new Error(`The picture is larger than ${maxBytes / (1024 * 1024)} MB.`)
}

/** The longest side of Bluesky's card picture: a link card is drawn small, so more is only bytes. */
const CARD_PICTURE_SIDE = 1200

/**
 * A small JPEG copy of a kept picture for Bluesky's link card (posts design §4.2c): at most
 * `maxBytes` (Bluesky takes 1,000,000), at most 1200 pixels on its longer side, made smaller and
 * plainer step by step until it fits. Made here because the server has no image library. Throws a
 * plain sentence when even the smallest step does not fit.
 */
export async function smallJpeg(file: Blob, maxBytes: number): Promise<Blob> {
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
      const factor = Math.min(1, CARD_PICTURE_SIDE / longer) * scale
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
