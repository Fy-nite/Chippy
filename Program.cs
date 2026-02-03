using VBlank;
using VBlank.AudioEngine;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Adamantite.GFX;
using System;

namespace Chippy
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            using var engine = new AsmoGameEngine("Chippy", 800, 500, 1.9f);
            // Host the native converted Chippy game inside AsmoGameEngine
            engine.HostNativeGame(new Chippy.Game());
            engine.Run();
        }
    }
}
