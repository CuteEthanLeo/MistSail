using MistSail.Consts;
using MistSail.Utils;
using Sheas_Core;
using System;

namespace MistSail.Proces;

internal class NginxProc : Proc
{
    internal NginxProc() : base(MainConst.NginxPath) { }

    protected override async void Process_Exited(object sender, EventArgs e) => await NginxCleaner.Clean();
}