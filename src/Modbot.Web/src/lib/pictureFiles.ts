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
