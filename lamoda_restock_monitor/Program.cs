using Discord;
using Discord.Webhook;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace lamoda_restock_monitor
{
    class Program
    {
        static List<string> proxies = new List<string>();
        static Random rnd = new Random();
        static int delay;
        static string webhook = "";
        static List<string> skus = new List<string>();

        static void Main(string[] args)
        {
            FileStream file = new FileStream("proxies.txt", FileMode.Open);
            StreamReader readFile = new StreamReader(file);
            while (!readFile.EndOfStream)
            {
                proxies.Add(readFile.ReadLine());
            }
            readFile.Close();

            Console.Write("\n   Введите webhook (discord): ");
            webhook = Console.ReadLine();
            Console.Write("\n   Введите задержку (мс): ");
            delay = int.Parse(Console.ReadLine());
            Console.WriteLine("\n   Монитор запущен!\n");
            Task.Run(() => ReadSKU("sku.txt")); // Отдельный таск, чтобы параллельно мониторились изменения пидов в txt
            LamodaApiResponse previousResponse = null;

            while (true)
            {
                List<string> skuList = skus;
                string skuQuery = string.Join(",", skuList);
                string apiUrl = "https://www.lamoda.ru/api/v1/products/get?skus=" + skuQuery;
                LamodaApiResponse newResponse = SendApiRequest(apiUrl);

                if (previousResponse != null)
                {
                    CompareResponses(previousResponse, newResponse);
                }

                previousResponse = newResponse;

                Thread.Sleep(delay);
            }
        }

        public static List<string> ReadSKU(string filePath)
        {
            while (true)
            {
                try
                {
                    using (StreamReader sr = new StreamReader(filePath))
                    {
                        string line;
                        skus.Clear();
                        while ((line = sr.ReadLine()) != null)
                        {
                            lock (skus)
                            {
                                skus.Add(line);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error reading SKUs from the file: " + ex.Message);
                }

                Thread.Sleep(60000); // ОБновление пидов раз в минуту
            }
        }

        public static LamodaApiResponse SendApiRequest(string apiUrl)
        {
            int proxyNum = rnd.Next(0, proxies.Count);
            string proxy = proxies[proxyNum];
            string[] proxyString = proxy.Split(':');
            string ip = proxyString[0];
            int port = Int32.Parse(proxyString[1]);
            string user = proxyString[2];
            string pass = proxyString[3];

            var handler = new HttpClientHandler();
            WebProxy proxyObject = new WebProxy("http://" + ip + ":" + port + "/", false);
            proxyObject.Credentials = new NetworkCredential(user, pass);
            handler.Proxy = proxyObject; // Формат прокси ip:port:log:pass

            try
            {
                using (HttpClient httpClient = new HttpClient(handler))
                {
                    httpClient.DefaultRequestHeaders.Add("Accept-Language", "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");
                    httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/117.0.0.0 Safari/537.36");
                    httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");

                    HttpResponseMessage response = httpClient.GetAsync(apiUrl).Result;
                    if (response.IsSuccessStatusCode)
                    {
                        string json = response.Content.ReadAsStringAsync().Result;
                        return JsonConvert.DeserializeObject<LamodaApiResponse>(json);
                    }
                    else
                    {
                        Console.WriteLine($"API request failed with status code: {response.StatusCode}");
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("An error occurred while making the API request: " + ex.Message);
                return null;
            }
        }


        public class Product
        {
            public Brand Brand { get; set; }
            public string Price { get; set; }
            public List<Size> Sizes { get; set; }
            public string Sku { get; set; }
            public string Thumbnail { get; set; }
            public string Model_Title { get; set; } // Иногда может быть null
        }

        public class Brand
        {
            public string Title { get; set; }
        }

        public class Size
        {
            public int Stock_Quantity { get; set; }
            public string Title { get; set; }
        }

        public class LamodaApiResponse
        {
            public List<Product> Products { get; set; }
        }


        private static void CompareResponses(LamodaApiResponse oldResponse, LamodaApiResponse newResponse)
        {
            foreach (Product newProduct in newResponse.Products)
            {
                Product oldProduct = oldResponse.Products.Find(p => p.Sku == newProduct.Sku);

                if (oldProduct == null)
                {
                    string sizes = "";
                    foreach (Size size in newProduct.Sizes)
                    {
                        sizes += $"{size.Title} [{size.Stock_Quantity}]\n";
                    }
                    sizes = sizes.TrimEnd('\n', '\0');

                    if (newProduct.Price == null)
                    {
                        newProduct.Price = "N/A";
                    }

                    if (sizes == "")
                    {
                        sizes = "N/A";
                    }

                    sendWebhook("New SKU added", webhook, $"{newProduct.Thumbnail}", $"{newProduct.Brand.Title} {newProduct.Model_Title}", $"{newProduct.Sku}", sizes, $"{newProduct.Price}");
                }

                if (oldProduct != null)
                {
                    foreach (Size newSize in newProduct.Sizes)
                    {
                        Size oldSize = oldProduct.Sizes.Find(s => s.Title == newSize.Title);

                        if (oldSize != null && newSize.Stock_Quantity > 0 && oldSize.Stock_Quantity < 1)
                        {
                            string sizes = "";
                            foreach (Size size in newProduct.Sizes)
                            {
                                if (size.Stock_Quantity > 0)
                                {
                                    sizes += $"{size.Title} [{size.Stock_Quantity}]\n";
                                }
                            }
                            sizes = sizes.TrimEnd('\n', '\0');

                            sendWebhook("Restock", webhook, $"{newProduct.Thumbnail}", $"{newProduct.Brand.Title} {newProduct.Model_Title}", $"{newProduct.Sku}", sizes, $"{newProduct.Price} ₸");
                        }
                    }
                }
            }
        }

        static void sendWebhook(string status, string webhook, string image, string name, string url, string sizes, string price)
        {
            try
            {
                List<Discord.EmbedFieldBuilder> Fields;

                var clientDiscord = new DiscordWebhookClient(webhook);
                var embedAuthor = new EmbedAuthorBuilder
                {
                    Name = "lamoda.ru",
                    Url = "https://lamoda.ru/",
                };

                var embedField1 = new EmbedFieldBuilder
                {
                    Name = "Sizes",
                    Value = sizes,
                    IsInline = true,
                };

                var embedField2 = new EmbedFieldBuilder
                {
                    Name = "Price",
                    Value = price,
                    IsInline = true,
                };

                var embedField3 = new EmbedFieldBuilder
                {
                    Name = "Status",
                    Value = status,
                    IsInline = false,
                };

                Fields = new List<Discord.EmbedFieldBuilder>() { embedField1, embedField2, embedField3 };

                var embedFooter = new EmbedFooterBuilder
                {
                    IconUrl = "https://images-ext-1.discordapp.net/external/VqAAsMzqfedAQD4n1wF8LCW7tiJA98B3iseROKgGSuo/https/i.imgur.com/ZqSZr3Q.png",
                    Text = "SResellera • @zhabraiI",
                };

                var embed = new EmbedBuilder

                {
                    Description = "[" + name + "](https://www.lamoda.ru/p/" + url + "/sresellera)",
                    Author = embedAuthor,
                    ThumbnailUrl = "https://a.lmcdn.ru/img236x341" + image,
                    Fields = Fields,
                    Footer = embedFooter,
                };

                clientDiscord.SendMessageAsync(text: "", embeds: new[] { embed.Build() });
            }
            catch (Exception ex)
            {
                Console.WriteLine("An error occurred while sending webhook: " + ex.Message);
            }
        }
    }
}
