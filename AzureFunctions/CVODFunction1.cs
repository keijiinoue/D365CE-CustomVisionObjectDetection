using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.CognitiveServices.Vision.CustomVision.Prediction;
using Microsoft.Azure.CognitiveServices.Vision.CustomVision.Prediction.Models;
using System.Collections.Generic;
using System.IO;
using System.Drawing;
using Newtonsoft.Json;

namespace CustomVisionObjectDetectionFunction1
{
    /// <summary>
    /// 画像データを受け取って Custom Vision Object Detection を利用して検知し、検知した画像を処理した画像を返す。
    /// 検知のパーセンテージ数値のしきい値は設けていない。
    /// Custom Vision Object Detection を 専用APIでコールする。
    /// D365 CE の JavaScript Web Resource から呼び出されることを想定している。
    /// 
    /// POSTで受け取るデータは、以下のような JSON データを想定している。
    /// {
    ///   "mimetype": "image/jpeg",
    ///   "documentbody": "/9j/4AAQSkZJRgABAQEASABIAAD/4SlMRXhpZgAATU0AKgAAAAgADQALAAIAAAAmAAAItgEP・・・省略・・・345orOTFoz/2Q=="
    /// }
    /// 
    /// 戻り値はBASE64形式の画像データで、mimetype は入力時と変わらない。
    /// 
    /// Function6bとの違いは、fontsizeWidthRatio をパラメーターとして受け取ることだけ。
    /// 
    /// </summary>
    public static class CVODFunction1
    {
        #region Settings for YOUR Custom Vision Object Detection
        const string projectId = "xxxxxxxxxxx"; // Prediction URL の Prediction/ の後に続く/までの文字列
        const string iterationId = "xxxxxxxxxxx"; // Prediction URL の iterationId= の後に続く文字列
        const string predictionKey = "xxxxxxxxxxxx"; // ヘッダー Prediction-Key に設定すべきと記載のある文字列
        #endregion
        static float fontsizeWidthRatio = 0.02f; // 画像の幅に対するフォントサイズの比率の既定値。この数値が大きいほど、フォントサイズが大きくなる。
        [FunctionName("CVODFunction1")]
        public static async Task<HttpResponseMessage> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)]HttpRequestMessage req, TraceWriter log)
        {
            log.Info("C# HTTP trigger function processed a request.");

            string jsonContent = await req.Content.ReadAsStringAsync();
            //log.Info(jsonContent);

            dynamic data = JsonConvert.DeserializeObject(jsonContent);
            string mimetype = data.mimetype;
            string documentbody = data.documentbody; // BASE64の画像データ
            fontsizeWidthRatio = data.fontsizeWidthRatio;

            log.Info($"documentbody.Length: {documentbody.Length}, mimetype: {mimetype}");

            if (documentbody.Length > 0 && mimetype != "")
            {
                try
                {
                    IList<PredictionModel> predictions = GetPredictions(documentbody, log);
                    string base64Image = GetPredictedBase64Image(documentbody, mimetype, predictions, log);
                    log.Info($"Got base64Image. base64Image.Length: {base64Image.Length}");

                    return req.CreateResponse(HttpStatusCode.OK, base64Image);
                }
                catch (Exception ex)
                {
                    log.Info($"Error {ex.Message}");

                    return req.CreateResponse(HttpStatusCode.OK, "Error {ex.Message}");
                }
            }
            else
            {
                log.Info($"Exiting as not matching for (documentbody.Length > 0 && mimetype != \"\")");

                return req.CreateErrorResponse(HttpStatusCode.OK, "Exiting as not matching for (documentbody.Length > 0 && mimetype != \"\")");
            }
        }

        /// <summary>
        /// 検知した結果を表す描画を施した画像の Base64 文字列を返す。
        /// 上手くいかなかった場合には、""を返す。
        /// </summary>
        /// <param name="documentbody"></param>
        /// <param name="predictions"></param>
        /// <returns></returns>
        private static string GetPredictedBase64Image(string documentbody, string mimetype, IList<PredictionModel> predictions, TraceWriter log)
        {
            Image image;
            using (MemoryStream ms = new MemoryStream(Convert.FromBase64String(documentbody)))
            {
                image = Image.FromStream(ms);
                Bitmap bitmap = new Bitmap(image);
                log.Info($"Got bitmap. bitmap.Width: {bitmap.Width}, bitmap.Height: {bitmap.Height}");

                Graphics g = Graphics.FromImage(image);
                // 検出した領域を示すための矩形の色のリスト
                List<Color> colors = new List<Color> {
                    Color.Blue,
                    Color.Red,
                    Color.Purple,
                    Color.Green,
                    Color.Cyan,
                    Color.Yellow
                };
                // 降順にソート
                List<PredictionModel> sortedPredictions = predictions.OrderByDescending(p => p.Probability).ToList();

                for (int i = 0; i < sortedPredictions.Count; i++)
                {
                    var prediction = sortedPredictions[i];
                    var color = colors[i % colors.Count];
                    Pen p = new Pen(color, bitmap.Width / 200);
                    // 検出した領域の矩形
                    g.DrawRectangle(
                        p,
                        bitmap.Width * (float)prediction.BoundingBox.Left,
                        bitmap.Height * (float)prediction.BoundingBox.Top,
                        bitmap.Width * (float)prediction.BoundingBox.Width,
                        bitmap.Height * (float)prediction.BoundingBox.Height);
                    Font font = new Font("Segoe UI", bitmap.Width * fontsizeWidthRatio);
                    string text = $"{prediction.TagName}: {prediction.Probability:P1}";
                    // 文字列の描画サイズ
                    SizeF textSize = g.MeasureString(text, font);
                    // 文字列のための背景の矩形（その場所と左上の2か所）
                    g.FillRectangle(
                        new SolidBrush(color),
                            bitmap.Width * (float)prediction.BoundingBox.Left,
                            bitmap.Height * (float)(prediction.BoundingBox.Top + prediction.BoundingBox.Height),
                            textSize.Width,
                            textSize.Height);
                    g.FillRectangle(
                        new SolidBrush(color),
                            0,
                            i * textSize.Height,
                            textSize.Width,
                            textSize.Height);
                    // 文字列（その場所と左上の2か所）
                    g.DrawString(
                        text,
                        font,
                        Brushes.White,
                        bitmap.Width * (float)prediction.BoundingBox.Left,
                        bitmap.Height * (float)(prediction.BoundingBox.Top + prediction.BoundingBox.Height));
                    g.DrawString(
                        text,
                        font,
                        Brushes.White,
                        0,
                        i * textSize.Height);
                }
            }
            if (image != null)
            {
                log.Info("Got predicted image");
                using (MemoryStream ms = new MemoryStream())
                {
                    image.Save(ms, getImageFormatFromMIMEtype(mimetype));
                    log.Info("Image saved");
                    return Convert.ToBase64String(ms.ToArray());
                }
            }

            return "";
        }
        private static System.Drawing.Imaging.ImageFormat getImageFormatFromMIMEtype(string mimetype)
        {
            switch (mimetype)
            {
                case "image/gif":
                    return System.Drawing.Imaging.ImageFormat.Gif;
                case "image/png":
                    return System.Drawing.Imaging.ImageFormat.Png;
                case "image/jpeg":
                    return System.Drawing.Imaging.ImageFormat.Jpeg;
                case "image/bmp":
                    return System.Drawing.Imaging.ImageFormat.Bmp;
                default:
                    throw new Exception($"mimetype '{mimetype}' is not supported by this function.");
            }
        }
        private static IList<PredictionModel> GetPredictions(string documentbody, TraceWriter log)
        {
            PredictionEndpoint endpoint = new PredictionEndpoint() { ApiKey = predictionKey };
            log.Info("Making prediction(s):");
            IList<PredictionModel> predictions;

            using (MemoryStream ms = new MemoryStream(Convert.FromBase64String(documentbody)))
            {
                var result = endpoint.PredictImage(new Guid(projectId), ms, iterationId: new Guid(iterationId));
                predictions = result.Predictions;

                foreach (var p in result.Predictions)
                {
                    log.Info($"\t{p.TagName}: {p.Probability:P1} [ {p.BoundingBox.Left}, {p.BoundingBox.Top}, {p.BoundingBox.Width}, {p.BoundingBox.Height} ]");
                }
            }
            log.Info("Got prediction(s):");

            return predictions;
        }
    }
}
