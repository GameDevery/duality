﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Drawing;

using Duality;
using Duality.Drawing;
using Duality.Components;
using Duality.Resources;
using Duality.Plugins.Tilemaps;
using Duality.Editor.Plugins.Tilemaps.Properties;
using Duality.Editor.Plugins.Tilemaps.UndoRedoActions;
using Duality.Editor.Plugins.CamView.CamViewStates;
using Duality.Editor.Plugins.CamView.CamViewLayers;


namespace Duality.Editor.Plugins.Tilemaps.CamViewStates
{
	public class TilemapEditorCamViewState : CamViewState
	{
		private enum TilemapTool
		{
			None,

			Select,
			Brush,
			Rect,
			Oval,
			Fill
		}
		private enum ContinuousAction
		{
			None,

			DrawTile,
			FillTileRect,
			FillTileOval
		}

		private static readonly Point2 InvalidTile = new Point2(-1, -1);

		private TilemapTool      selectedTool     = TilemapTool.Brush;
		private Tilemap          selectedTilemap  = null;
		private TilemapRenderer  hoveredRenderer  = null;
		private Point2           hoveredTile      = InvalidTile;
		private TilemapTool      activeTool       = TilemapTool.None;
		private Tilemap          activeTilemap    = null;
		private Point2           activeAreaOrigin = InvalidTile;
		private Grid<bool>       activeArea       = new Grid<bool>();
		private ContinuousAction action           = ContinuousAction.None;
		private Point2           actionBeginTile  = InvalidTile;

		private Grid<bool>       activeFillBuffer       = new Grid<bool>();
		private List<Vector2[]>  activeAreaOutlineCache = new List<Vector2[]>();

		private ToolStrip        toolstrip        = null;
		private ToolStripButton  toolButtonSelect = null;
		private ToolStripButton  toolButtonBrush  = null;
		private ToolStripButton  toolButtonRect   = null;
		private ToolStripButton  toolButtonOval   = null;
		private ToolStripButton  toolButtonFill   = null;


		public override string StateName
		{
			get { return "Tilemap Editor"; }
		}


		public TilemapEditorCamViewState()
		{
			this.SetDefaultObjectVisibility(
				typeof(Tilemap),
				typeof(TilemapRenderer));
			this.SetDefaultActiveLayers(
				typeof(GridCamViewLayer));
		}
		
		private void SetActiveTool(TilemapTool tool)
		{
			this.selectedTool = tool;
			this.UpdateToolbar();
			this.OnMouseMove();
		}

		private Tilemap QuerySelectedTilemap()
		{
			// Detect whether the user has either selected a Tilemap directly, 
			// or a related Component that points to an external one
			return
				DualityEditorApp.Selection.Components.OfType<Tilemap>().FirstOrDefault() ?? 
				DualityEditorApp.Selection.GameObjects.GetComponents<Tilemap>().FirstOrDefault() ??
				DualityEditorApp.Selection.Components.OfType<TilemapRenderer>().Select(r => r.ExternalTilemap).FirstOrDefault() ?? 
				DualityEditorApp.Selection.GameObjects.GetComponents<TilemapRenderer>().Select(r => r.ExternalTilemap).FirstOrDefault();
		}
		private IEnumerable<TilemapRenderer> QueryVisibleTilemapRenderers()
		{
			var all = Scene.Current.FindComponents<TilemapRenderer>();
			return all.Where(r => 
				r.Active && 
				!DesignTimeObjectData.Get(r.GameObj).IsHidden && 
				this.IsCoordInView(r.GameObj.Transform.Pos, r.BoundRadius));
		}
		
		private void UpdateToolbar()
		{
			this.toolButtonSelect.Checked = false;
			this.toolButtonBrush.Checked  = false;
			this.toolButtonRect.Checked   = false;
			this.toolButtonOval.Checked   = false;
			this.toolButtonFill.Checked   = false;

			switch (this.selectedTool)
			{
				case TilemapTool.Select: this.toolButtonSelect.Checked = true; break;
				case TilemapTool.Brush:  this.toolButtonBrush.Checked  = true; break;
				case TilemapTool.Rect:   this.toolButtonRect.Checked   = true; break;
				case TilemapTool.Oval:   this.toolButtonOval.Checked   = true; break;
				case TilemapTool.Fill:   this.toolButtonFill.Checked   = true; break;
			}
		}
		private void UpdateCursor()
		{
			switch (this.activeTool)
			{
				case TilemapTool.None:   this.Cursor = TilemapsResCache.CursorTileSelect;       break;
				case TilemapTool.Select: this.Cursor = TilemapsResCache.CursorTileSelectActive; break;
				case TilemapTool.Brush:  this.Cursor = TilemapsResCache.CursorTileBrush;        break;
				case TilemapTool.Rect:   this.Cursor = TilemapsResCache.CursorTileRect;         break;
				case TilemapTool.Oval:   this.Cursor = TilemapsResCache.CursorTileOval;         break;
				case TilemapTool.Fill:   this.Cursor = TilemapsResCache.CursorTileFill;         break;
			}
		}
		private void UpdateHoverState(Point cursorPos)
		{
			Point2 lastHoveredTile = this.hoveredTile;
			TilemapRenderer lastHoveredRenderer = this.hoveredRenderer;

			// Reset hover data
			this.hoveredTile = InvalidTile;
			this.hoveredRenderer = null;

			// Early-out, if a camera action claims the cursor
			if (this.CamActionRequiresCursor)
			{
				if (lastHoveredTile != this.hoveredTile || lastHoveredRenderer != this.hoveredRenderer)
					this.Invalidate();
				return;
			}

			// Determine which renderers we're able to see right now and sort them by their Z values
			TilemapRenderer[] visibleRenderers = this.QueryVisibleTilemapRenderers().ToArray();
			visibleRenderers.StableSort((a, b) =>
			{
				// The currently edited tilemap always prevails
				if (this.selectedTool != TilemapTool.Select && a.ActiveTilemap == this.selectedTilemap && a.ActiveTilemap != b.ActiveTilemap)
					return -1;
				// Otherwise, do regular Z sorting
				else
					return (a.GameObj.Transform.Pos.Z > b.GameObj.Transform.Pos.Z) ? 1 : -1;
			});

			// While doing an action, it's either the selected tilemap or none. No switch inbetween.
			if (this.action != ContinuousAction.None && visibleRenderers.Length > 0)
			{
				visibleRenderers = new TilemapRenderer[] { visibleRenderers[0] };
			}

			// Iterate over visible tilemap renderers to find out what the cursor is hovering
			for (int i = 0; i < visibleRenderers.Length; i++)
			{
				TilemapRenderer renderer = visibleRenderers[i];
				Transform transform = renderer.GameObj.Transform;

				// Determine where the cursor is hovering in various coordinate systems
				Vector3 worldCursorPos = this.CameraComponent.GetSpaceCoord(new Vector3(cursorPos.X, cursorPos.Y, transform.Pos.Z));
				Vector2 localCursorPos = transform.GetLocalPoint(worldCursorPos.Xy);
				Point2 tileCursorPos = renderer.GetTileAtLocalPos(localCursorPos);

				// If we're hovering a tile of the current renderer, we're done
				if (tileCursorPos.X != -1 && tileCursorPos.Y != -1)
				{
					if (!DesignTimeObjectData.Get(renderer.GameObj).IsLocked)
					{
						this.hoveredTile = tileCursorPos;
						this.hoveredRenderer = renderer;
					}
					break;
				}
			}

			// If we're not doing an action, let our action begin tile just follow around
			if (this.action == ContinuousAction.None)
				this.actionBeginTile = this.hoveredTile;

			// If something changed, redraw the view
			if (lastHoveredTile != this.hoveredTile || lastHoveredRenderer != this.hoveredRenderer)
			{
				this.Invalidate();
			}
		}
		private void UpdateActiveState()
		{
			TilemapTool lastActiveTool = this.activeTool;
			Tilemap lastActiveTilemap = this.activeTilemap;

			// Determine what action the cursor would do in the current state
			if (this.hoveredRenderer == null)
				this.activeTool = TilemapTool.None;
			else if (this.selectedTilemap != null && this.hoveredRenderer != null && this.hoveredRenderer.ActiveTilemap != this.selectedTilemap)
				this.activeTool = TilemapTool.Select;
			else
				this.activeTool = this.selectedTool;

			if (this.activeTool != TilemapTool.None)
				this.activeTilemap = this.hoveredRenderer.ActiveTilemap ?? this.selectedTilemap;

			// Determine the area that is affected by the current action
			switch (this.activeTool)
			{
				case TilemapTool.None:
				{
					this.activeAreaOutlineCache.Clear();
					this.activeAreaOrigin = this.hoveredTile;
					this.activeArea.Resize(0, 0);
					break;
				}
				case TilemapTool.Select:
				case TilemapTool.Brush:
				{
					this.activeAreaOutlineCache.Clear();
					this.activeAreaOrigin = this.hoveredTile;
					this.activeArea.Resize(1, 1);
					this.activeArea[0, 0] = true;
					break;
				}
				case TilemapTool.Rect:
				{
					Point2 topLeft = new Point2(
						Math.Min(this.actionBeginTile.X, this.hoveredTile.X),
						Math.Min(this.actionBeginTile.Y, this.hoveredTile.Y));
					Point2 size = new Point2(
						1 + Math.Abs(this.actionBeginTile.X - this.hoveredTile.X),
						1 + Math.Abs(this.actionBeginTile.Y - this.hoveredTile.Y));
					this.activeAreaOutlineCache.Clear();
					this.activeAreaOrigin = topLeft;
					this.activeArea.Resize(size.X, size.Y);
					this.activeArea.Fill(true, 0, 0, size.X, size.Y);
					break;
				}
				case TilemapTool.Oval:
				{
					Point2 topLeft = new Point2(
						Math.Min(this.actionBeginTile.X, this.hoveredTile.X),
						Math.Min(this.actionBeginTile.Y, this.hoveredTile.Y));
					Point2 size = new Point2(
						1 + Math.Abs(this.actionBeginTile.X - this.hoveredTile.X),
						1 + Math.Abs(this.actionBeginTile.Y - this.hoveredTile.Y));
					Vector2 radius = (Vector2)size * 0.5f;
					Vector2 offset = new Vector2(0.5f, 0.5f) - radius;

					// Adjust to receive nicer low-res shapes
					radius.X -= 0.1f;
					radius.Y -= 0.1f;

					this.activeAreaOutlineCache.Clear();
					this.activeAreaOrigin = topLeft;
					this.activeArea.Resize(size.X, size.Y);
					for (int y = 0; y < size.Y; y++)
					{
						for (int x = 0; x < size.X; x++)
						{
							Vector2 relative = new Vector2(x, y) + offset;
							this.activeArea[x, y] = 
								((relative.X * relative.X) / (radius.X * radius.X)) + 
								((relative.Y * relative.Y) / (radius.Y * radius.Y)) <= 1.0f;
						}
					}
					break;
				}
				case TilemapTool.Fill:
				{
					// Don't update flood fill when still inside the previous flood fill area
					Point2 activeLocalHover = new Point2(
						this.hoveredTile.X - this.activeAreaOrigin.X, 
						this.hoveredTile.Y - this.activeAreaOrigin.Y);
					bool hoverInsideActiveRect = (
						activeLocalHover.X > 0 && 
						activeLocalHover.Y > 0 &&
						activeLocalHover.X < this.activeArea.Width &&
						activeLocalHover.Y < this.activeArea.Height);
					bool hoverInsideActiveArea = (hoverInsideActiveRect && this.activeArea[activeLocalHover.X, activeLocalHover.Y]);
					if (hoverInsideActiveArea)
						break;

					// Run the flood fill algorithm
					Grid<Tile> tiles = this.activeTilemap.BeginUpdateTiles();
					FloodFillTiles(ref this.activeFillBuffer, tiles, this.hoveredTile);
					this.activeTilemap.EndUpdateTiles(0, 0, 0, 0);

					// Find the filled areas boundaries and copy it to the active area
					Point2 filledAreaSize;
					this.activeFillBuffer.GetContentBoundaries(out this.activeAreaOrigin, out filledAreaSize);
					this.activeArea.Resize(filledAreaSize.X, filledAreaSize.Y);
					this.activeFillBuffer.CopyTo(this.activeArea, 0, 0, -1, -1, this.activeAreaOrigin.X, this.activeAreaOrigin.Y);
					this.activeAreaOutlineCache.Clear();
					break;
				}
			}

			// If our highlighted action changed, redraw view and update the cursor
			if (lastActiveTool != this.activeTool || lastActiveTilemap != this.activeTilemap)
			{
				this.UpdateCursor();
				this.Invalidate();
			}
		}

		private void PerformDrawTiles(EditTilemapActionType actionType, Tilemap tilemap, Point2 pos, Grid<bool> brush, Tile tile)
		{
			Grid<Tile> drawPatch = new Grid<Tile>(brush.Width, brush.Height);
			drawPatch.Fill(tile, 0, 0, brush.Width, brush.Height);

			UndoRedoManager.Do(new EditTilemapAction(
				tilemap, 
				actionType, 
				pos, 
				drawPatch,
				brush));
		}

		private void BeginContinuousAction(ContinuousAction action)
		{
			if (this.action == action) return;
			this.action = action;
			this.actionBeginTile = this.activeAreaOrigin;

			if (this.action == ContinuousAction.DrawTile)
			{
				this.PerformDrawTiles(
					EditTilemapActionType.DrawTile, 
					this.activeTilemap, 
					this.activeAreaOrigin, 
					this.activeArea, 
					new Tile { Index = 1 });
			}
		}
		private void UpdateContinuousAction()
		{
			if (this.action == ContinuousAction.DrawTile)
			{
				this.PerformDrawTiles(
					EditTilemapActionType.DrawTile, 
					this.activeTilemap, 
					this.activeAreaOrigin, 
					this.activeArea, 
					new Tile { Index = 1 });
			}
		}
		private void EndContinuousAction()
		{
			if (this.action == ContinuousAction.None) return;

			if (this.action == ContinuousAction.FillTileRect ||
				this.action == ContinuousAction.FillTileOval)
			{
				EditTilemapActionType type = 
					(this.action == ContinuousAction.FillTileRect) ? 
					EditTilemapActionType.FillRect : 
					EditTilemapActionType.FillOval;
				this.PerformDrawTiles(
					EditTilemapActionType.FillRect,
					this.activeTilemap, 
					this.activeAreaOrigin, 
					this.activeArea, 
					new Tile { Index = 2 });
			}

			this.action = ContinuousAction.None;
			this.actionBeginTile = InvalidTile;
			UndoRedoManager.Finish();
		}

		private void DrawTileHighlights(Canvas canvas, TilemapRenderer renderer, ColorRgba color, Point2 origin, Grid<bool> highlight, List<Vector2[]> outlineCache = null)
		{
			if (highlight.Capacity == 0) return;

			BatchInfo defaultMaterial = canvas.State.Material;
			BatchInfo highlightMaterial = this.FgColor.GetLuminance() > 0.5f ? 
				new BatchInfo(DrawTechnique.Light, ColorRgba.White.WithAlpha(0.5f)) :
				new BatchInfo(DrawTechnique.Alpha, ColorRgba.White);

			Transform transform = renderer.GameObj.Transform;
			Tilemap tilemap = renderer.ActiveTilemap;
			Tileset tileset = tilemap != null ? tilemap.Tileset.Res : null;
			Vector2 tileSize = tileset != null ? tileset.TileSize : Tileset.DefaultTileSize;
			Rect localRect = renderer.LocalTilemapRect;

			// Determine the object's local coordinate system (rotated, scaled) in world space
			Vector2 worldAxisX = Vector2.UnitX;
			Vector2 worldAxisY = Vector2.UnitY;
			MathF.TransformCoord(ref worldAxisX.X, ref worldAxisX.Y, transform.Angle, transform.Scale);
			MathF.TransformCoord(ref worldAxisY.X, ref worldAxisY.Y, transform.Angle, transform.Scale);

			Vector2 localOriginPos = tileSize * origin;
			Vector2 worldOriginPos = localOriginPos.X * worldAxisX + localOriginPos.Y * worldAxisY;

			canvas.PushState();
			{
				// Configure the canvas so our shapes are properly rotated and scaled
				canvas.State.TransformHandle = -localRect.TopLeft;
				canvas.State.TransformAngle = transform.Angle;
				canvas.State.TransformScale = new Vector2(transform.Scale);
				canvas.State.ZOffset = -0.01f;
				
				// Fill all highlighted tiles that are currently visible
				{
					canvas.State.ColorTint = color * this.FgColor.WithAlpha(0.25f);
					canvas.State.SetMaterial(highlightMaterial);
				
					// Determine tile visibility
					Vector2 worldTilemapOriginPos = localRect.TopLeft;
					MathF.TransformCoord(ref worldTilemapOriginPos.X, ref worldTilemapOriginPos.Y, transform.Angle, transform.Scale);
					TilemapCulling.TileInput cullingIn = new TilemapCulling.TileInput
					{
						// Remember: All these transform values are in world space
						TilemapPos = transform.Pos + new Vector3(worldTilemapOriginPos) + new Vector3(worldOriginPos),
						TilemapScale = transform.Scale,
						TilemapAngle = transform.Angle,
						TileCount = new Point2(highlight.Width, highlight.Height),
						TileSize = tileSize
					};
					TilemapCulling.TileOutput cullingOut = TilemapCulling.GetVisibleTileRect(canvas.DrawDevice, cullingIn);
					int renderedTileCount = cullingOut.VisibleTileCount.X * cullingOut.VisibleTileCount.Y;

					// Draw all visible highlighted tiles
					{
						Point2 tileGridPos = cullingOut.VisibleTileStart;
						Vector2 renderStartPos = worldOriginPos + tileGridPos.X * tileSize.X * worldAxisX + tileGridPos.Y * tileSize.Y * worldAxisY;;
						Vector2 renderPos = renderStartPos;
						Vector2 tileXStep = worldAxisX * tileSize.X;
						Vector2 tileYStep = worldAxisY * tileSize.Y;
						int lineMergeCount = 0;
						int totalRects = 0;
						for (int tileIndex = 0; tileIndex < renderedTileCount; tileIndex++)
						{
							bool current = highlight[tileGridPos.X, tileGridPos.Y];
							if (current)
							{
								// Try to merge consecutive rects in the same line to reduce drawcalls / CPU load
								bool hasNext = (tileGridPos.X + 1 < highlight.Width) && ((tileGridPos.X + 1 - cullingOut.VisibleTileStart.X) < cullingOut.VisibleTileCount.X);
								bool next = hasNext ? highlight[tileGridPos.X + 1, tileGridPos.Y] : false;
								if (next)
								{
									lineMergeCount++;
								}
								else
								{
									totalRects++;
									canvas.FillRect(
										transform.Pos.X + renderPos.X - lineMergeCount * tileXStep.X, 
										transform.Pos.Y + renderPos.Y - lineMergeCount * tileXStep.Y, 
										transform.Pos.Z,
										tileSize.X * (1 + lineMergeCount), 
										tileSize.Y);
									lineMergeCount = 0;
								}
							}

							tileGridPos.X++;
							renderPos += tileXStep;
							if ((tileGridPos.X - cullingOut.VisibleTileStart.X) >= cullingOut.VisibleTileCount.X)
							{
								tileGridPos.X = cullingOut.VisibleTileStart.X;
								tileGridPos.Y++;
								renderPos = renderStartPos;
								renderPos += tileYStep * (tileGridPos.Y - cullingOut.VisibleTileStart.Y);
							}
						}
					}
				}

				// Determine the outlines of individual highlighted tile patches
				if (outlineCache == null) outlineCache = new List<Vector2[]>();
				if (outlineCache.Count == 0)
				{
					GetTileAreaOutlines(highlight, tileSize, ref outlineCache);
				}

				// Draw outlines around all highlighted tile patches
				canvas.State.ColorTint = color * this.FgColor;
				canvas.State.SetMaterial(defaultMaterial);
				foreach (Vector2[] outline in outlineCache)
				{
					canvas.DrawPolygon(
						outline,
						transform.Pos.X + worldOriginPos.X, 
						transform.Pos.Y + worldOriginPos.Y, 
						transform.Pos.Z);
				}

			}
			canvas.PopState();
		}

		protected override void OnEnterState()
		{
			base.OnEnterState();

			// Init the custom tile editing toolbar
			{
				this.View.SuspendLayout();
				this.toolstrip = new ToolStrip();
				this.toolstrip.SuspendLayout();

				this.toolstrip.GripStyle = System.Windows.Forms.ToolStripGripStyle.Hidden;
				this.toolstrip.Name = "toolstrip";
				this.toolstrip.Text = "Tilemap Editor Tools";

				this.toolButtonSelect = new ToolStripButton(TilemapsRes.ItemName_TileSelect, TilemapsResCache.IconTileSelect, this.toolButtonSelect_Click);
				this.toolButtonSelect.DisplayStyle = ToolStripItemDisplayStyle.Image;
				this.toolButtonSelect.AutoToolTip = true;
				this.toolstrip.Items.Add(this.toolButtonSelect);

				this.toolButtonBrush = new ToolStripButton(TilemapsRes.ItemName_TileBrush, TilemapsResCache.IconTileBrush, this.toolButtonBrush_Click);
				this.toolButtonBrush.DisplayStyle = ToolStripItemDisplayStyle.Image;
				this.toolButtonBrush.AutoToolTip = true;
				this.toolstrip.Items.Add(this.toolButtonBrush);

				this.toolButtonRect = new ToolStripButton(TilemapsRes.ItemName_TileRect, TilemapsResCache.IconTileRect, this.toolButtonRect_Click);
				this.toolButtonRect.DisplayStyle = ToolStripItemDisplayStyle.Image;
				this.toolButtonRect.AutoToolTip = true;
				this.toolstrip.Items.Add(this.toolButtonRect);

				this.toolButtonOval = new ToolStripButton(TilemapsRes.ItemName_TileOval, TilemapsResCache.IconTileOval, this.toolButtonOval_Click);
				this.toolButtonOval.DisplayStyle = ToolStripItemDisplayStyle.Image;
				this.toolButtonOval.AutoToolTip = true;
				this.toolstrip.Items.Add(this.toolButtonOval);

				this.toolButtonFill = new ToolStripButton(TilemapsRes.ItemName_TileFill, TilemapsResCache.IconTileFill, this.toolButtonFill_Click);
				this.toolButtonFill.DisplayStyle = ToolStripItemDisplayStyle.Image;
				this.toolButtonFill.AutoToolTip = true;
				this.toolstrip.Items.Add(this.toolButtonFill);

				this.toolstrip.Renderer = new Duality.Editor.Controls.ToolStrip.DualitorToolStripProfessionalRenderer();
				this.toolstrip.BackColor = Color.FromArgb(212, 212, 212);

				this.View.Controls.Add(this.toolstrip);
				this.View.Controls.SetChildIndex(this.toolstrip, this.View.Controls.IndexOf(this.View.ToolbarCamera));
				this.toolstrip.ResumeLayout(true);
				this.View.ResumeLayout(true);
			}

			// Register events
			DualityEditorApp.SelectionChanged += this.DualityEditorApp_SelectionChanged;
			DualityEditorApp.ObjectPropertyChanged += this.DualityEditorApp_ObjectPropertyChanged;

			// Initial update
			this.SetActiveTool(this.selectedTool);
			this.UpdateToolbar();
		}
		protected override void OnLeaveState()
		{
			base.OnLeaveState();

			// Cleanup the custom tile editing toolbar
			{
				this.toolstrip.Dispose();
				this.toolButtonSelect.Dispose();
				this.toolButtonBrush.Dispose();
				this.toolButtonRect.Dispose();
				this.toolButtonOval.Dispose();
				this.toolButtonFill.Dispose();
				this.toolstrip = null;
				this.toolButtonSelect = null;
				this.toolButtonBrush = null;
				this.toolButtonRect = null;
				this.toolButtonOval = null;
				this.toolButtonFill = null;
			}

			// Unregister events
			DualityEditorApp.SelectionChanged -= this.DualityEditorApp_SelectionChanged;
			DualityEditorApp.ObjectPropertyChanged -= this.DualityEditorApp_ObjectPropertyChanged;

			// Reset state
			this.Cursor = CursorHelper.Arrow;
		}

		protected override void OnMouseMove(MouseEventArgs e)
		{
			base.OnMouseMove(e);
			Point2 lastHoverTile = this.hoveredTile;

			// Determine what the cursor is hovering over and what actions it could perform
			this.UpdateHoverState(e.Location);
			this.UpdateActiveState();

			// If we're performing a continuous action, update it when our hover tile changes
			if (this.hoveredTile != lastHoverTile)
			{
				this.UpdateContinuousAction();
			}
		}
		protected override void OnMouseLeave(EventArgs e)
		{
			base.OnMouseLeave(e);
			this.hoveredTile = InvalidTile;
			this.hoveredRenderer = null;
			this.activeTool = TilemapTool.None;
			this.activeTilemap = null;
			this.activeAreaOrigin = InvalidTile;
			this.activeArea.Resize(0, 0);
			this.UpdateCursor();
			this.Invalidate();
		}
		protected override void OnMouseDown(MouseEventArgs e)
		{
			base.OnMouseDown(e);
			if (e.Button == MouseButtons.Left)
			{
				// Begin a continuous action, if one is associated with the currently active tool
				ContinuousAction newAction = GetContinuousActionOfTool(this.activeTool);
				if (newAction != ContinuousAction.None)
				{
					if (this.selectedTilemap != this.activeTilemap)
						DualityEditorApp.Select(this, new ObjectSelection(this.activeTilemap.GameObj));
					this.BeginContinuousAction(newAction);
				}
				// If the fill tool is selected, directly perform the fill operation
				else if (this.activeTool == TilemapTool.Fill)
				{
					this.PerformDrawTiles(EditTilemapActionType.FloodFill, this.activeTilemap, this.activeAreaOrigin, this.activeArea, new Tile { Index = 4 });
					// Clear our buffered fill tool state
					this.activeArea.Clear();
				}
				// Otherwise, do a selection or deselection
				else if (this.activeTool == TilemapTool.Select)
					DualityEditorApp.Select(this, new ObjectSelection(this.hoveredRenderer.ActiveTilemap.GameObj));
				else if (this.activeTool == TilemapTool.None)
					DualityEditorApp.Deselect(this, ObjectSelection.Category.GameObjCmp);
			}
		}
		protected override void OnMouseUp(MouseEventArgs e)
		{
			base.OnMouseUp(e);
			this.EndContinuousAction();
		}
		protected override void OnKeyDown(KeyEventArgs e)
		{
			base.OnKeyDown(e);

			if (Control.ModifierKeys == Keys.None)
			{
				if (e.KeyCode == Keys.Q && this.toolButtonSelect.Enabled)
					this.toolButtonSelect_Click(this, EventArgs.Empty);
				else if (e.KeyCode == Keys.W && this.toolButtonBrush.Enabled)
					this.toolButtonBrush_Click(this, EventArgs.Empty);
				else if (e.KeyCode == Keys.E && this.toolButtonRect.Enabled)
					this.toolButtonRect_Click(this, EventArgs.Empty);
				else if (e.KeyCode == Keys.R && this.toolButtonOval.Enabled)
					this.toolButtonOval_Click(this, EventArgs.Empty);
				else if (e.KeyCode == Keys.T && this.toolButtonFill.Enabled)
					this.toolButtonFill_Click(this, EventArgs.Empty);
			}
		}
		protected override void OnCamActionRequiresCursorChanged(EventArgs e)
		{
			base.OnCamActionRequiresCursorChanged(e);
			this.OnMouseMove();
		}

		protected override void OnRenderState()
		{
			// "Grey out" all non-selected Tilemap Renderers
			Dictionary<TilemapRenderer,ColorRgba> oldColors = null;
			if (this.selectedTilemap != null)
			{
				foreach (TilemapRenderer renderer in Scene.Current.FindComponents<TilemapRenderer>())
				{
					if (renderer.ActiveTilemap == this.selectedTilemap)
						continue;

					if (oldColors == null)
						oldColors = new Dictionary<TilemapRenderer,ColorRgba>();

					oldColors[renderer] = renderer.ColorTint;
					renderer.ColorTint = renderer.ColorTint.WithAlpha(0.33f);
				}
			}

			// Do all the regular state rendering
			base.OnRenderState();

			// Reset each renderer's color tint value
			if (oldColors != null)
			{
				foreach (var pair in oldColors)
				{
					pair.Key.ColorTint = pair.Value;
				}
			}
		}
		protected override void OnCollectStateDrawcalls(Canvas canvas)
		{
			base.OnCollectStateDrawcalls(canvas);

			TilemapRenderer[] visibleRenderers = this.QueryVisibleTilemapRenderers().ToArray();
			for (int i = 0; i < visibleRenderers.Length; i++)
			{
				TilemapRenderer renderer = visibleRenderers[i];
				Transform transform = renderer.GameObj.Transform;

				Tilemap tilemap = renderer.ActiveTilemap;
				Tileset tileset = tilemap != null ? tilemap.Tileset.Res : null;
				Rect localRect = renderer.LocalTilemapRect;
				bool greyOut = this.selectedTilemap != null && this.selectedTilemap != tilemap;

				// Configure the canvas so our shapes are properly rotated and scaled
				canvas.State.TransformHandle = -localRect.TopLeft;
				canvas.State.TransformAngle = transform.Angle;
				canvas.State.TransformScale = new Vector2(transform.Scale);
				canvas.State.ZOffset = -0.01f;

				// Draw the surrounding rect of the tilemap
				canvas.State.ColorTint = this.FgColor.WithAlpha(greyOut ? 0.33f : 1.0f);
				canvas.DrawRect(
					transform.Pos.X, 
					transform.Pos.Y, 
					transform.Pos.Z,
					localRect.W, 
					localRect.H);

				// Highlight the currently active tiles
				if (this.activeTilemap == renderer.ActiveTilemap)
				{
					this.DrawTileHighlights(
						canvas, 
						renderer, 
						ColorRgba.White, 
						this.activeAreaOrigin, 
						this.activeArea, 
						this.activeAreaOutlineCache);
				}
			}
		}

		private void DualityEditorApp_SelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			if (e.SameObjects) return;
			if (!e.AffectedCategories.HasFlag(ObjectSelection.Category.GameObjCmp))
				return;

			// Tilemap selection changed
			Tilemap newSelection = this.QuerySelectedTilemap();
			if (this.selectedTilemap != newSelection)
			{
				this.selectedTilemap = newSelection;
				if (this.Mouseover)
					this.OnMouseMove();
				this.Invalidate();
			}
		}
		private void DualityEditorApp_ObjectPropertyChanged(object sender, ObjectPropertyChangedEventArgs e)
		{
			if (e.HasProperty(TilemapsReflectionInfo.Property_Tilemap_Tiles))
			{
				this.Invalidate();
			}
		}

		private void toolButtonSelect_Click(object sender, EventArgs e)
		{
			this.SetActiveTool(TilemapTool.Select);
		}
		private void toolButtonBrush_Click(object sender, EventArgs e)
		{
			this.SetActiveTool(TilemapTool.Brush);
		}
		private void toolButtonRect_Click(object sender, EventArgs e)
		{
			this.SetActiveTool(TilemapTool.Rect);
		}
		private void toolButtonOval_Click(object sender, EventArgs e)
		{
			this.SetActiveTool(TilemapTool.Oval);
		}
		private void toolButtonFill_Click(object sender, EventArgs e)
		{
			this.SetActiveTool(TilemapTool.Fill);
		}

		private static ContinuousAction GetContinuousActionOfTool(TilemapTool tool)
		{
			switch (tool)
			{
				default:                return ContinuousAction.None;
				case TilemapTool.Brush: return ContinuousAction.DrawTile;
				case TilemapTool.Rect:  return ContinuousAction.FillTileRect;
				case TilemapTool.Oval:  return ContinuousAction.FillTileOval;
			}
		}
		
		private static void GetTileAreaOutlines(Grid<bool> tileArea, Vector2 tileSize, ref List<Vector2[]> outlines)
		{
			// Initialize the container we'll put our outlines into
			if (outlines == null)
				outlines = new List<Vector2[]>();
			else
				outlines.Clear();

			// Generate a data structure containing all visible edges
			TileEdgeMap edgeMap = new TileEdgeMap(tileArea.Width + 1, tileArea.Height + 1);
			for (int y = 0; y < edgeMap.Height; y++)
			{
				for (int x = 0; x < edgeMap.Width; x++)
				{
					// Determine highlight state of the four tiles around this node
					bool topLeft     = x > 0              && y > 0               && tileArea[x - 1, y - 1];
					bool topRight    = x < tileArea.Width && y > 0               && tileArea[x    , y - 1];
					bool bottomLeft  = x > 0              && y < tileArea.Height && tileArea[x - 1, y    ];
					bool bottomRight = x < tileArea.Width && y < tileArea.Height && tileArea[x    , y    ];

					// Determine which edges are visible
					if (topLeft     != topRight   ) edgeMap.AddEdge(new Point2(x, y), new Point2(x    , y - 1));
					if (topRight    != bottomRight) edgeMap.AddEdge(new Point2(x, y), new Point2(x + 1, y    ));
					if (bottomRight != bottomLeft ) edgeMap.AddEdge(new Point2(x, y), new Point2(x    , y + 1));
					if (bottomLeft  != topLeft    ) edgeMap.AddEdge(new Point2(x, y), new Point2(x - 1, y    ));
				}
			}

			// Traverse edges to form outlines until no more edges are left
			RawList<Vector2> outlineBuilder = new RawList<Vector2>();
			while (true)
			{
				// Find the beginning of an outline
				Point2 current = edgeMap.FindNonEmpty();
				if (current.X == -1 || current.Y == -1) break;

				// Traverse it until no more edges are left
				while (true)
				{
					Point2 next = edgeMap.GetClockwiseNextFrom(current);
					if (next.X == -1 || next.Y == -1) break;

					outlineBuilder.Add(next * tileSize);
					edgeMap.RemoveEdge(current, next);
					current = next;
				}

				// If we have enough vertices, keep the outline for drawing
				Vector2[] outline = new Vector2[outlineBuilder.Count];
				outlineBuilder.CopyTo(outline, 0);
				outlines.Add(outline);

				// Reset the outline builder to an empty state
				outlineBuilder.Clear();
			}
		}
		
		/// <summary>
		/// Performs a flood fill operation originating from the specified position. 
		/// <see cref="Tile"/> equality is checked in the <see cref="_FloodFill_TilesEqual"/> method.
		/// </summary>
		/// <param name="fillBuffer">A buffer that will be filled with the result of the flood fill operation.</param>
		/// <param name="tiles"></param>
		/// <param name="pos"></param>
		private static void FloodFillTiles(ref Grid<bool> fillBuffer, Grid<Tile> tiles, Point2 pos)
		{
			// ## Note: ##
			// This flood fill algorithm is a modified version of "A More Efficient Flood Fill" by Adam Milazzo.
			// All credit for the original idea and sample implementation goes to him. Last seen on the web here:
			// http://adammil.net/blog/v126_A_More_Efficient_Flood_Fill.html
			// ###########

			// Initialize fill buffer
			if (fillBuffer == null)
				fillBuffer = new Grid<bool>(tiles.Width, tiles.Height);
			else if (fillBuffer.Width != tiles.Width || fillBuffer.Height != tiles.Height)
				fillBuffer.Resize(tiles.Width, tiles.Height);
			fillBuffer.Clear();

			// Get the base tile for comparison
			Tile baseTile = tiles[pos.X, pos.Y];

			// Find the topleft-most tile to start with
			pos = _FloodFillTiles_FindTopLeft(fillBuffer, tiles, pos, baseTile);
			// Run the main part of the algorithm
			_FloodFillTiles(fillBuffer, tiles, pos, baseTile);
		}
		private static void _FloodFillTiles(Grid<bool> fillBuffer, Grid<Tile> tiles, Point2 pos, Tile baseTile)
		{
			// Since the top and left of the current tile are blocking the fill operation, proceed down and right
			int lastRowLength = 0;
			do
			{
				Point2 rowPos = pos;
				int rowLength = 0;

				// Narrow scan line width on the left when necessary
				if (lastRowLength != 0 && !_FloodFill_IsCandidate(fillBuffer, tiles, pos, baseTile))
				{
					do
					{
						pos.X++;
						lastRowLength--;
					}
					while (lastRowLength != 0 && !_FloodFill_IsCandidate(fillBuffer, tiles, pos, baseTile));

					rowPos.X = pos.X;
				}
				// Expand scan line width to the left when necessary
				else
				{
					for (; pos.X != 0 && _FloodFill_IsCandidate(fillBuffer, tiles, new Point2(pos.X - 1, pos.Y), baseTile); rowLength++, lastRowLength++)
					{
						pos.X--;
						fillBuffer[pos.X, pos.Y] = true;

						// If something above the current scan line is free, handle it recursively
						if (pos.Y != 0 && _FloodFill_IsCandidate(fillBuffer, tiles, new Point2(pos.X, pos.Y - 1), baseTile))
						{
							// Find the topleft-most tile to start with
							Point2 targetPos = new Point2(pos.X, pos.Y - 1);
							targetPos = _FloodFillTiles_FindTopLeft(fillBuffer, tiles, targetPos, baseTile);
							_FloodFillTiles(fillBuffer, tiles, targetPos, baseTile);
						}
					}
				}
				
				// Fill the current row
				for (; rowPos.X < tiles.Width && _FloodFill_IsCandidate(fillBuffer, tiles, rowPos, baseTile); rowLength++, rowPos.X++)
					fillBuffer[rowPos.X, rowPos.Y] = true;

				// If the current row is shorter than the previous, see if there are 
				// disconnected pixels below the (filled) previous row left to handle
				if (rowLength < lastRowLength)
				{
					for (int end = pos.X + lastRowLength; ++rowPos.X < end; )
					{
						// Recursively handle the disconnected below-bottom pixels of the last row
						if (_FloodFill_IsCandidate(fillBuffer, tiles, rowPos, baseTile)) 
							_FloodFillTiles(fillBuffer, tiles, rowPos, baseTile);
					}
				}
				// If the current row is longer than the previous, see if there are 
				// top pixels above this one that are disconnected from the last row
				else if (rowLength > lastRowLength && pos.Y != 0)
				{
					for (int prevRowX = pos.X + lastRowLength; ++prevRowX < rowPos.X; )
					{
						// Recursively handle the disconnected pixels of the last row
						if (_FloodFill_IsCandidate(fillBuffer, tiles, new Point2(prevRowX, pos.Y - 1), baseTile))
						{
							// Find the topleft-most tile to start with
							Point2 targetPos = new Point2(prevRowX, pos.Y - 1);
							targetPos = _FloodFillTiles_FindTopLeft(fillBuffer, tiles, targetPos, baseTile);
							_FloodFillTiles(fillBuffer, tiles, targetPos, baseTile);
						}
					}
				}

				lastRowLength = rowLength;
			}
			while (lastRowLength != 0 && ++pos.Y < tiles.Height);
		}
		private static Point2 _FloodFillTiles_FindTopLeft(Grid<bool> fillBuffer, Grid<Tile> tiles, Point2 pos, Tile baseTile)
		{
			// Find the topleft-most connected matching tile
			while(true)
			{
				Point2 origin = pos;
				while (pos.Y != 0 && _FloodFill_IsCandidate(fillBuffer, tiles, new Point2(pos.X, pos.Y - 1), baseTile)) pos.Y--;
				while (pos.X != 0 && _FloodFill_IsCandidate(fillBuffer, tiles, new Point2(pos.X - 1, pos.Y), baseTile)) pos.X--;
				if (pos == origin) break;
			}
			return pos;
		}
		private static bool _FloodFill_TilesEqual(Tile baseTile, Tile otherTile)
		{
			return baseTile.Index == otherTile.Index;
		}
		private static bool _FloodFill_IsCandidate(Grid<bool> fillBuffer, Grid<Tile> tiles, Point2 pos, Tile baseTile)
		{
			return !fillBuffer[pos.X, pos.Y] && _FloodFill_TilesEqual(baseTile, tiles[pos.X, pos.Y]);
		}
	}
}
