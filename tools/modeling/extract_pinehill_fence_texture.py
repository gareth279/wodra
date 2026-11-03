from pathlib import Path
import argparse,cv2,numpy as np
parser=argparse.ArgumentParser(description='Extract the photographed Pine Hill weathered fence from a local frame.')
parser.add_argument('--frame',type=Path,required=True);parser.add_argument('--output',type=Path,required=True);args=parser.parse_args()
image=cv2.imread(str(args.frame),cv2.IMREAD_COLOR)
if image is None or image.shape[1]<3800:raise SystemExit('Expected 4K video frame.')
# The selected right-hand run contains only the weathered palings. It avoids the
# sign pole, concrete utility pole, foreground tree, driveway and passing vehicle.
quad=np.float32([[1745,510],[3500,492],[3500,790],[1745,808]])
width,height=4096,512;target=np.float32([[0,0],[width-1,0],[width-1,height-1],[0,height-1]])
homography=cv2.getPerspectiveTransform(quad,target)
texture=cv2.warpPerspective(image,homography,(width,height),flags=cv2.INTER_CUBIC,borderMode=cv2.BORDER_REPLICATE)
mask=np.zeros(image.shape[:2],np.uint8)
# Remove the small weed attached to the fence without removing the timber boards.
cv2.fillPoly(mask,[np.int32([[3320,690],[3388,690],[3388,760],[3320,760]])],255)
mask=cv2.warpPerspective(mask,homography,(width,height),flags=cv2.INTER_NEAREST)
texture=cv2.inpaint(texture,mask,10,cv2.INPAINT_TELEA)
# The upper few rectified pixels contain a narrow line of distant foliage above
# the board top. Trim it away so the material contains timber only.
texture=texture[45:height-24,110:width-100]
texture=cv2.resize(texture,(width,height),interpolation=cv2.INTER_CUBIC)
args.output.parent.mkdir(parents=True,exist_ok=True)
cv2.imwrite(str(args.output),texture,[cv2.IMWRITE_JPEG_QUALITY,94])
print(f'wrote fence-only {width}x{height}: {args.output}')
