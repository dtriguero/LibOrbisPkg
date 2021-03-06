﻿using LibOrbisPkg.Util;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Design.Serialization;
using System.IO;
using System.Linq;

namespace LibOrbisPkg.PFS
{
  /// <summary>
  /// Contains the functionality to construct a PFS disk image.
  /// </summary>
  public class PfsBuilder
  {
    static int CeilDiv(int a, int b) => a / b + (a % b == 0 ? 0 : 1);
    static long CeilDiv(long a, long b) => a / b + (a % b == 0 ? 0 : 1);

    private PfsHeader hdr;
    private List<inode> inodes;
    private List<PfsDirent> super_root_dirents;

    private inode super_root_ino, fpt_ino;

    private FSDir root;

    private List<FSDir> allDirs;
    private List<FSFile> allFiles;
    private List<FSNode> allNodes;

    private FlatPathTable fpt;

    private PfsProperties properties;

    private int emptyBlock = 0x4;

    private struct BlockSigInfo
    {
      public long Block;
      public long SigOffset;
      public int Size;
      public BlockSigInfo(long block, long offset, int size = 0x10000)
      {
        Block = block;
        SigOffset = offset;
        Size = size;
      }
    }
    private Stack<BlockSigInfo> sig_order = new Stack<BlockSigInfo>();

    Action<string> logger;
    private void Log(string s) => logger?.Invoke(s);

    public PfsBuilder(PfsProperties p, Action<string> logger = null)
    {
      this.logger = logger;
      properties = p;
      Setup();
    }

    public long CalculatePfsSize()
    {
      return hdr.Ndblock * hdr.BlockSize;
    }

    void Setup()
    {
      // TODO: Combine the superroot-specific stuff with the rest of the data block writing.
      // I think this is as simple as adding superroot and flat_path_table to allNodes

      // This doesn't seem to really matter when verifying a PKG so use all zeroes for now
      var seed = new byte[16];
      // Insert header digest to be calculated with the rest of the digests
      sig_order.Push(new BlockSigInfo(0, 0x380, 0x5A0));
      hdr = new PfsHeader {
        BlockSize = properties.BlockSize,
        ReadOnly = 1,
        Mode = (properties.Sign ? PfsMode.Signed : 0) 
             | (properties.Encrypt ? PfsMode.Encrypted : 0)
             | PfsMode.UnknownFlagAlwaysSet,
        UnknownIndex = 1,
        Seed = properties.Encrypt || properties.Sign ? seed : null
      };
      inodes = new List<inode>();

      Log("Setting up root structure...");
      SetupRootStructure();
      allDirs = root.GetAllChildrenDirs();
      allFiles = root.GetAllChildrenFiles();
      allNodes = new List<FSNode>(allDirs);
      allNodes.AddRange(allFiles);

      Log(string.Format("Creating directory inodes ({0})...", allDirs.Count));
      addDirInodes();

      Log(string.Format("Creating file inodes ({0})...", allFiles.Count));
      addFileInodes();

      Log("Creating flat_path_table...");
      fpt = new FlatPathTable(allNodes);

      Log("Calculating data block layout...");
      allNodes.Insert(0, root);
      CalculateDataBlockLayout();
    }

    public void WriteImage(Stream stream)
    {
      Log("Writing header...");
      hdr.WriteToStream(stream);
      Log("Writing inodes...");
      WriteInodes(stream);
      Log("Writing superroot dirents");
      WriteSuperrootDirents(stream);

      var fpt_file = new FSFile(s => fpt.WriteToStream(s), "flat_path_table", fpt.Size);
      fpt_file.ino = fpt_ino;
      allNodes.Insert(0, fpt_file);

      Log("Writing data blocks...");
      for (var x = 0; x < allNodes.Count; x++)
      {
        var f = allNodes[x];
        stream.Position = f.ino.StartBlock * hdr.BlockSize;
        WriteFSNode(stream, f);
      }
      stream.SetLength(hdr.Ndblock * hdr.BlockSize);

      if (hdr.Mode.HasFlag(PfsMode.Signed))
      {
        Log("Signing...");
        var signKey = Crypto.PfsGenSignKey(properties.EKPFS, hdr.Seed);
        foreach (var sig in sig_order)
        {
          var sig_buffer = new byte[sig.Size];
          stream.Position = sig.Block * properties.BlockSize;
          stream.Read(sig_buffer, 0, sig.Size);
          stream.Position = sig.SigOffset;
          stream.Write(Crypto.HmacSha256(signKey, sig_buffer), 0, 32);
          stream.WriteLE((int)sig.Block);
        }
      }

      if (hdr.Mode.HasFlag(PfsMode.Encrypted))
      {
        Log("Encrypting...");
        var encKey = Crypto.PfsGenEncKey(properties.EKPFS, hdr.Seed);
        var dataKey = new byte[16];
        var tweakKey = new byte[16];
        Buffer.BlockCopy(encKey, 0, tweakKey, 0, 16);
        Buffer.BlockCopy(encKey, 16, dataKey, 0, 16);
        stream.Position = hdr.BlockSize;
        var transformer = new XtsBlockTransform(dataKey, tweakKey);
        const int sectorSize = 0x1000;
        long xtsSector = 16;
        long totalSectors = (stream.Length + 0xFFF) / sectorSize;
        byte[] sectorBuffer = new byte[sectorSize];
        while (xtsSector < totalSectors)
        {
          if(xtsSector / 0x10 == emptyBlock)
          {
            xtsSector += 16;
          }
          stream.Position = xtsSector * sectorSize;
          stream.Read(sectorBuffer, 0, sectorSize);
          transformer.EncryptSector(sectorBuffer, (ulong)xtsSector);
          stream.Position = xtsSector * sectorSize;
          stream.Write(sectorBuffer, 0, sectorSize);
          xtsSector += 1;
        }
      }
    }

    /// <summary>
    /// Adds inodes for each dir.
    /// </summary>
    void addDirInodes()
    {
      inodes.Add(root.ino);
      foreach (var dir in allDirs)
      {
        var ino = MakeInode(
          Mode: InodeMode.dir | InodeMode.rx_only,
          Number: (uint)inodes.Count,
          Blocks: 1,
          Size: 65536,
          Flags: InodeFlags.@readonly,
          Nlink: 2 // 1 link each for its own dirent and its . dirent
        );
        dir.ino = ino;
        dir.Dirents.Add(new PfsDirent { Name = ".", InodeNumber = ino.Number, Type = DirentType.Dot });
        dir.Dirents.Add(new PfsDirent { Name = "..", InodeNumber = dir.Parent.ino.Number, Type = DirentType.DotDot });

        var dirent = new PfsDirent { Name = dir.name, InodeNumber = (uint)inodes.Count, Type = DirentType.Directory };
        dir.Parent.Dirents.Add(dirent);
        dir.Parent.ino.Nlink++;
        inodes.Add(ino);
      }
    }

    /// <summary>
    /// Adds inodes for each file.
    /// </summary>
    void addFileInodes()
    {
      foreach (var file in allFiles.OrderBy(x => x.FullPath()))
      {
        var ino = MakeInode(
          Mode: InodeMode.file | InodeMode.rx_only,
          Size: file.Size,
          SizeCompressed: file.CompressedSize,
          Number: (uint)inodes.Count,
          Blocks: (uint)CeilDiv(file.Size, hdr.BlockSize),
          Flags: InodeFlags.@readonly | (file.Compress ? InodeFlags.compressed : 0)
        );
        if (properties.Sign) // HACK: Outer PFS images don't use readonly?
        {
          ino.Flags &= ~InodeFlags.@readonly;
        }
        file.ino = ino;
        var dirent = new PfsDirent { Name = file.name, Type = DirentType.File, InodeNumber = (uint)inodes.Count };
        file.Parent.Dirents.Add(dirent);
        inodes.Add(ino);
      }
    }

    long roundUpSizeToBlock(long size) => CeilDiv(size, hdr.BlockSize) * hdr.BlockSize;
    long calculateIndirectBlocks(long size)
    {
      var sigs_per_block = hdr.BlockSize / 36;
      var blocks = CeilDiv(size, hdr.BlockSize);
      var ib = 0L;
      if (blocks > 12)
      {
        blocks -= 12;
        ib++;
      }
      if (blocks > sigs_per_block)
      {
        blocks -= sigs_per_block;
        ib += 1 + CeilDiv(blocks, sigs_per_block);
      }
      return ib;
    }

    /// <summary>
    /// Sets the data blocks. Also updates header for total number of data blocks.
    /// </summary>
    void CalculateDataBlockLayout()
    {
      long inoNumberToOffset(uint number, int db = 0)
        => hdr.BlockSize + (DinodeS32.SizeOf * number) + 0x64 + (36 * db);
      if (properties.Sign)
      {
        // Include the header block in the total count
        hdr.Ndblock = 1;
        var inodesPerBlock = hdr.BlockSize / DinodeS32.SizeOf;
        hdr.DinodeCount = inodes.Count;
        hdr.DinodeBlockCount = CeilDiv(inodes.Count, inodesPerBlock);
        hdr.InodeBlockSig.Blocks = (uint)hdr.DinodeBlockCount;
        hdr.InodeBlockSig.Size = hdr.DinodeBlockCount * hdr.BlockSize;
        hdr.InodeBlockSig.SizeCompressed = hdr.DinodeBlockCount * hdr.BlockSize;
        hdr.InodeBlockSig.SetTime(properties.FileTime);
        hdr.InodeBlockSig.Flags = 0;
        for (var i = 0; i < hdr.DinodeBlockCount; i++)
        {
          hdr.InodeBlockSig.SetDirectBlock(i, 1 + i);
          sig_order.Push(new BlockSigInfo(1 + i, 0xB8 + (36 * i)));
        }
        hdr.Ndblock += hdr.DinodeBlockCount;
        super_root_ino.SetDirectBlock(0, (int)(hdr.DinodeBlockCount + 1));
        sig_order.Push(new BlockSigInfo(super_root_ino.StartBlock, inoNumberToOffset(super_root_ino.Number)));
        hdr.Ndblock += super_root_ino.Blocks;

        // flat path table
        fpt_ino.SetDirectBlock(0, super_root_ino.StartBlock + 1);
        fpt_ino.Size = fpt.Size;
        fpt_ino.SizeCompressed = fpt.Size;
        fpt_ino.Blocks = (uint)CeilDiv(fpt.Size, hdr.BlockSize);
        sig_order.Push(new BlockSigInfo(fpt_ino.StartBlock, inoNumberToOffset(fpt_ino.Number)));

        for (int i = 1; i < fpt_ino.Blocks && i < 12; i++)
        {
          fpt_ino.SetDirectBlock(i, (int)hdr.Ndblock++);
          sig_order.Push(new BlockSigInfo(fpt_ino.StartBlock, inoNumberToOffset(fpt_ino.Number, i)));
        }

        // DATs I've found include an empty block after the FPT
        hdr.Ndblock++;
        // HACK: outer PFS has a block of zeroes that is not encrypted???
        emptyBlock = (int)hdr.Ndblock;
        hdr.Ndblock++;

        var ibStartBlock = hdr.Ndblock;
        hdr.Ndblock += allNodes.Select(s => calculateIndirectBlocks(s.Size)).Sum();

        var sigs_per_block = hdr.BlockSize / 36;
        // Fill in DB/IB pointers
        foreach (var n in allNodes)
        {
          var blocks = CeilDiv(n.Size, hdr.BlockSize);
          n.ino.SetDirectBlock(0, (int)hdr.Ndblock);
          n.ino.Blocks = (uint)blocks;
          n.ino.Size = n is FSDir ? roundUpSizeToBlock(n.Size) : n.Size;
          if (n.ino.SizeCompressed == 0)
            n.ino.SizeCompressed = n.ino.Size;

            for (var i = 0; (blocks - i) > 0 && i < 12; i++)
          {
            sig_order.Push(new BlockSigInfo((int)hdr.Ndblock++, inoNumberToOffset(n.ino.Number, i)));
          }
          if(blocks > 12)
          {
            // More than 12 blocks -> use 1 indirect block
            sig_order.Push(new BlockSigInfo(ibStartBlock, inoNumberToOffset(n.ino.Number, 12)));
            for(int i = 12, pointerOffset = 0; (blocks - i) > 0 && i < (12 + sigs_per_block); i++, pointerOffset += 36)
            {
              sig_order.Push(new BlockSigInfo((int)hdr.Ndblock++, ibStartBlock * hdr.BlockSize + pointerOffset));
            }
            ibStartBlock++;
          }
          if(blocks > 12 + sigs_per_block)
          {
            // More than 12 + one block of pointers -> use 1 doubly-indirect block + any number of indirect blocks
            sig_order.Push(new BlockSigInfo(ibStartBlock, inoNumberToOffset(n.ino.Number, 13)));
            for(var i = 12 + sigs_per_block; (blocks - i) > 0 && i < (12 + sigs_per_block + (sigs_per_block * sigs_per_block)); i += sigs_per_block)
            {
              sig_order.Push(new BlockSigInfo(ibStartBlock, inoNumberToOffset(n.ino.Number, 12)));
              for (int j = 0, pointerOffset = 0; (blocks - i - j) > 0 && j < sigs_per_block; j++, pointerOffset += 36)
              {
                sig_order.Push(new BlockSigInfo((int)hdr.Ndblock++, ibStartBlock * hdr.BlockSize + pointerOffset));
              }
              ibStartBlock++;
            }
          }
        }
      }
      else
      {
        // Include the header block in the total count
        hdr.Ndblock = 1;
        var inodesPerBlock = hdr.BlockSize /DinodeD32.SizeOf;
        hdr.DinodeCount = inodes.Count;
        hdr.DinodeBlockCount = CeilDiv(inodes.Count, inodesPerBlock);
        hdr.InodeBlockSig.Blocks = (uint)hdr.DinodeBlockCount;
        hdr.InodeBlockSig.Size = hdr.DinodeBlockCount * hdr.BlockSize;
        hdr.InodeBlockSig.SizeCompressed = hdr.DinodeBlockCount * hdr.BlockSize;
        hdr.InodeBlockSig.SetDirectBlock(0, (int)hdr.Ndblock++);
        hdr.InodeBlockSig.SetTime(properties.FileTime);
        for (var i = 1; i < hdr.DinodeBlockCount; i++)
        {
          hdr.InodeBlockSig.SetDirectBlock(i, -1);
          hdr.Ndblock++;
        }
        super_root_ino.SetDirectBlock(0, (int)hdr.Ndblock);
        hdr.Ndblock += super_root_ino.Blocks;

        // flat path table
        fpt_ino.SetDirectBlock(0, (int)hdr.Ndblock++);
        fpt_ino.Size = fpt.Size;
        fpt_ino.SizeCompressed = fpt.Size;
        fpt_ino.Blocks = (uint)CeilDiv(fpt.Size, hdr.BlockSize);

        for (int i = 1; i < fpt_ino.Blocks && i < 12; i++)
          fpt_ino.SetDirectBlock(i, (int)hdr.Ndblock++);
        // DATs I've found include an empty block after the FPT
        hdr.Ndblock++;

        // Calculate length of all dirent blocks
        foreach (var n in allNodes)
        {
          var blocks = CeilDiv(n.Size, hdr.BlockSize);
          n.ino.SetDirectBlock(0, (int)hdr.Ndblock);
          n.ino.Blocks = (uint)blocks;
          n.ino.Size = n is FSDir ? roundUpSizeToBlock(n.Size) : n.Size;
          if(n.ino.SizeCompressed == 0)
            n.ino.SizeCompressed = n.ino.Size;
          for (int i = 1; i < blocks && i < 12; i++)
          {
            n.ino.SetDirectBlock(i, -1);
          }
          hdr.Ndblock += blocks;
        }
      }
    }

    inode MakeInode(InodeMode Mode, uint Blocks, long Size = 0, long SizeCompressed = 0, ushort Nlink = 1, uint Number = 0, InodeFlags Flags = 0)
    {
      inode ret;
      if (properties.Sign)
      {
        ret = new DinodeS32()
        {
          Mode = Mode,
          Blocks = Blocks,
          Size = Size,
          SizeCompressed = SizeCompressed,
          Nlink = Nlink,
          Number = Number,
          Flags = Flags | InodeFlags.unk2 | InodeFlags.unk3,
        };
      }
      else
      {
        ret = new DinodeD32()
        {
          Mode = Mode,
          Blocks = Blocks,
          Size = Size,
          SizeCompressed = SizeCompressed,
          Nlink = Nlink,
          Number = Number,
          Flags = Flags
        };
      }
      ret.SetTime(properties.FileTime);
      return ret;
    }

    /// <summary>
    /// Creates inodes and dirents for superroot, flat_path_table, and uroot.
    /// Also, creates the root node for the FS tree.
    /// </summary>
    void SetupRootStructure()
    {
      inodes.Add(super_root_ino = MakeInode(
        Mode: InodeMode.dir | InodeMode.rx_only,
        Blocks: 1,
        Size: 65536,
        SizeCompressed: 65536,
        Nlink: 1,
        Number: 0,
        Flags: InodeFlags.@internal | InodeFlags.@readonly
      ));
      inodes.Add(fpt_ino = MakeInode(
        Mode: InodeMode.file | InodeMode.rx_only,
        Blocks: 1,
        Number: 1,
        Flags: InodeFlags.@internal | InodeFlags.@readonly
      ));
      var uroot_ino = MakeInode(
        Mode: InodeMode.dir | InodeMode.rx_only,
        Number: 2,
        Size: 65536,
        SizeCompressed: 65536,
        Blocks: 1,
        Flags: InodeFlags.@readonly,
        Nlink: 3
      );

      super_root_dirents = new List<PfsDirent>
      {
        new PfsDirent { InodeNumber = 1, Name = "flat_path_table", Type = DirentType.File },
        new PfsDirent { InodeNumber = 2, Name = "uroot", Type = DirentType.Directory }
      };

      root = properties.root;
      root.name = "uroot";
      root.ino = uroot_ino;
      root.Dirents = new List<PfsDirent>
      {
        new PfsDirent { Name = ".", Type = DirentType.Dot, InodeNumber = 2 },
        new PfsDirent { Name = "..", Type = DirentType.DotDot, InodeNumber = 2 }
      };
      if(properties.Sign) // HACK: Outer PFS lacks readonly flags
      {
        super_root_ino.Flags &= ~InodeFlags.@readonly;
        fpt_ino.Flags &= ~InodeFlags.@readonly;
        uroot_ino.Flags &= ~InodeFlags.@readonly;
      }
    }

    /// <summary>
    /// Writes all the inodes to the image file. 
    /// </summary>
    /// <param name="s"></param>
    void WriteInodes(Stream s)
    {
      s.Position = hdr.BlockSize;
      foreach (var di in inodes)
      {
        di.WriteToStream(s);
        if (s.Position % hdr.BlockSize > hdr.BlockSize - (properties.Sign ? DinodeS32.SizeOf : DinodeD32.SizeOf))
        {
          s.Position += hdr.BlockSize - (s.Position % hdr.BlockSize);
        }
      }
    }

    /// <summary>
    /// Writes the dirents for the superroot, which precede the flat_path_table.
    /// </summary>
    /// <param name="stream"></param>
    void WriteSuperrootDirents(Stream stream)
    {
      stream.Position = hdr.BlockSize * (hdr.DinodeBlockCount + 1);
      foreach (var d in super_root_dirents)
      {
        d.WriteToStream(stream);
      }
    }

    /// <summary>
    /// Writes all the data blocks.
    /// </summary>
    /// <param name="s"></param>
    void WriteFSNode(Stream s, FSNode f)
    {
      if (f is FSDir)
      {
        var dir = (FSDir)f;
        var startBlock = f.ino.StartBlock;
        foreach (var d in dir.Dirents)
        {
          d.WriteToStream(s);
          if (s.Position % hdr.BlockSize > hdr.BlockSize - PfsDirent.MaxSize)
          {
            s.Position = (++startBlock * hdr.BlockSize);
          }
        }
      }
      else if (f is FSFile)
      {
        var file = (FSFile)f;
        file.Write(s);
      }
    }
  }
}