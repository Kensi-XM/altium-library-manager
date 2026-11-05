= Getting started

# start | Welcome
Altium Library Manager works with the Access database behind an Altium DbLib: you browse its tables, see each component's symbol, footprint and 3D model, and change the rows, without opening Access or Altium.

## Your first five minutes
1. Press **Ctrl+O** and pick your `.accdb` or `.mdb` database. Its tables appear in the panel on the left.
2. Click a table. It opens in a temporary tab.
3. Click a row: the **Preview** shows its symbol, footprint and 3D model, and **Details** shows all its fields.
4. Type in **Search rows** (`Ctrl+F`) to find parts, for example `10k 0603`.
5. To change something, turn on the **Edit** switch (`Ctrl+E`), make the change, and press `Ctrl+S` to save it.

> Nothing reaches the database until you save. Everything you do before that can be taken back with `Ctrl+Z`.

## Where to go next
- [Finding parts](#search): search, sorting and columns
- [Editing rows](#editing): how changes, Save and Undo work
- [Add elements](#add-elements): many similar parts at once
- [Table templates](#templates): making new tables quickly
- [Keyboard shortcuts](#shortcuts)

Press `F1` in any window to get here; in a dialog, it opens the topic about that dialog.

# window | The main window
The window has four parts, and their sizes and places can be changed.

| Part | What it does |
| **Database** | The tables of the open database, with a filter box. Collapse it with the arrow in its header, or from **View > Database panel**. |
| **Tables** | One tab per open table, with the toolbar for searching and editing. |
| **Details** | All the fields of the selected row. You can read and change them here. |
| **Preview** | The symbol, footprint and 3D model of the selected row. |

## Layouts
**View > Layout** decides which part sits in the center. Drag the edges between the parts to resize them; each layout remembers its own sizes.

| Layout | Keys | Center, right, bottom |
| **Side preview** | `Ctrl+1` | Table, preview, row |
| **Row** | `Ctrl+2` | Row, preview, table |
| **Table** | `Ctrl+3` | Table, row, preview |
| **Preview** | `Ctrl+4` | Preview, row, table |

## Theme and size
**View > Theme** switches between System, Light and Dark. The size of everything in the window is set in [Options](#options), under Appearance.

The save, undo and redo buttons are at the top, next to the menus. The line at the bottom of the window tells you what just happened.

# databases | Databases and tables
## Opening a database
Use **File > Open database** (`Ctrl+O`). The app opens the Access file itself (`.accdb` or `.mdb`), not the `.DbLib` file. It reopens the last database and its tables at the next start, unless you turn that off in [Options](#options).

> Reading Access files needs the 64-bit Microsoft Access Database Engine. It comes with 64-bit Microsoft Access; otherwise install **Microsoft Access Database Engine 2016 Redistributable** (`accessdatabaseengine_X64.exe`) from the Microsoft website.

## Finding a table
Type in **Filter tables** to narrow the list. `Up` and `Down` pick a table, `Enter` opens it and `Esc` clears the filter. `Ctrl+F` goes to this box when the keyboard is in the database panel.

## Tabs
A click on a table opens it in a **temporary tab**, with its title in italics. The next table you click replaces it, so tabs don't pile up. Double-click a table or its tab to keep it open. Changing a table keeps its tab too.

- Drag a tab to move it.
- Middle-click a tab, or press `Ctrl+W`, to close it.
- Right-click a tab to pin it or to close several tabs at once. Pinned tabs stay at the start of the strip.
- `Ctrl+Tab` goes to the next tab, `Alt+1` to `Alt+8` go straight to a tab, and `Alt+9` to the last one.

## New and deleted tables
See [Creating tables](#new-tables). Right-click a table in the list to save it as a template or to delete it. A deleted table can be brought back: see [Deleted tables](#deleted-tables).

= Working with tables

# search | Finding parts
## Search rows
The search box finds rows as you type. A row is shown when it holds **every word** you typed, in any order and in any field, ignoring case.

| You type | It finds |
| `10k 0603` | Rows with both 10k and 0603, in any field |
| `"10 uF"` | The exact text in quotes |

The counter next to the box shows how many rows match, like `12 of 23,397 rows`.

- `Enter` goes to the table. `Esc` clears the search.
- `Up`, `Down`, `Page Up` and `Page Down` move through the rows while you stay in the box.

## Searching chosen columns
The **In:** button picks the columns to search, for example only Part Number. It is remembered for each table.

## Sorting
Click a column header to sort by it, and click it again to reverse the order. An arrow shows the direction.

To sort by several columns, click the first one, then hold `Shift` and click the next. The first stays the main one; the second puts in order the rows that are equal in the first. With `Shift`, clicking a column again reverses only that column.

> Try it on a column with repeats: click **Manufacturer**, then `Shift` + click **Value**. A column with no repeats gives the second one nothing to do.

## Column widths
Drag the edge of a column header to change its width; it is remembered for that table. Long values are cut off with dots: point at a cell to read all of it. Widths, text size and row height are in [Options](#options).

# editing | Editing rows
Tables open read-only, so browsing can't change anything. Turn on the **Edit** switch in the toolbar, or press `Ctrl+E`.

## How changes work
1. Change what you need: cells, whole rows, or fields in [Details](#details).
2. A star appears on the tab: the table has changes that aren't saved.
3. Press `Ctrl+S`, or the **Save** button, to write them to the database, all together. `Ctrl+Shift+S` saves all tables.

Saving is all or nothing: if the database refuses one row, nothing is written, and your changes stay in the table for you to correct.

> If you close a tab or the app with unsaved changes, you are asked whether to save them.

## Typing in a cell
Double-click a cell, or select it and start typing. `Tab` goes to the next cell. `Enter` ends the row, and `Esc` takes back what you typed in it.

Number, date and Yes/No fields only take a value of their kind. Numbers and dates are written as usual on this computer. In a Yes/No field you can type yes or no.

## Adding, copying and deleting rows
| Action | How | What it does |
| **Add row** | `Ctrl+Plus` | Starts a new row at the end of the table. A row left empty is dropped. |
| **Duplicate** | `Ctrl+D` | Makes a copy of the selected row. Its Part Number gets a number: `RES 10K (2)`. |
| **Delete** | `Ctrl+Minus` | Deletes the rows of the selected cells. |
| **Add elements** | toolbar | Adds many similar rows: see [Add elements](#add-elements). |

Fields that have a default value in Access start a new row with that value.

## Undo and Redo
`Ctrl+Z` takes back the last change and `Ctrl+Y` makes it again, also with the arrows at the top of the window or in the **Edit** menu. Each table has its own history, and one action on many cells is one step.

Undo also works after saving: the change is taken back in the table, and the next save writes that to the database.

## Part Numbers
Altium finds a component by its Part Number, so each one must be unique and not empty. The app refuses a Part Number that another row already has, ignoring case. When saving, it checks the database too, in case someone added the same one in the meantime.

Rows that already had the same Part Number stay as they are until you change them.

## Reading the table again
`F5` reads the table from the database, to show changes made in Access or Altium. If you have unsaved changes, you are asked what to do with them first.

# cells | Working with cells
Select cells by clicking and dragging, or with `Shift` and `Ctrl` as in a spreadsheet. `Ctrl+A` selects all of them. Right-click for everything you can do with the selection.

| Action | Keys | What it does |
| **Copy** | `Ctrl+C` | Copies the cells as text, which spreadsheets read too. Works with Edit off. |
| **Cut** | `Ctrl+X` | Copies, then clears the cells. |
| **Paste** | `Ctrl+V` | One value goes into all selected cells. A block goes in from the top left selected cell. |
| **Clear** | `Del` | Empties the selected cells. |
| **Fill cells** | menu | Types one value into all selected cells. |
| **Fill down** | menu | In each column, copies the first selected value into the cells below it. |
| **Select row, Select column** | menu | Extends the selection to whole rows, or selects a column. |

A change to many cells is checked first, and made in full or not at all. Each such change is one step of [Undo](#editing). AutoNumber fields are filled in by Access and can't be changed.

> Paste a block from Excel straight into the table: for example, a column of prices, or a few columns of part data.

# details | The Details panel
Details lists all the fields of the selected row, the Altium fields first. It follows the table: click a cell and its field is selected here, and the other way around.

## Changing values
With **Edit** on, click a value and type. `Enter` applies it, and `Esc` drops it. The change joins the table's other changes: it shows at once in the table and in the preview, `Ctrl+Z` takes it back, and `Ctrl+S` saves it.

## The buttons next to the fields
| Field | The button |
| **Library Path, Footprint Path** | Picks a library file. It is stored relative to the database folder when the other rows do so. |
| **Library Ref, Footprint Ref** | Lists the symbols or footprints of the row's library, to pick one. Type to filter, `Enter` applies, `Esc` closes. |
| **ComponentLink URL, other paths** | Picks a file. |

The same is in the right-click menu of the cell in the table: **Choose file** or **Choose from library**.

## Opening links
Hold `Ctrl` and click a value to open it: a web address opens in your browser, and a document such as a PDF datasheet opens in its program. Any other file is shown in its folder, for safety.

# preview | Preview of symbols and footprints
The preview shows the selected row's symbol, footprint and 3D model.

| In the view | What it does |
| Mouse wheel | Zooms in and out |
| Dragging | Moves the view |
| Double-click | Fits the component into the view |

The libraries are found through **Library Path** and **Footprint Path**. A path can be full or relative, or just a file name: the app also looks in the folders next to the database.

If a view stays empty, see [Solving problems](#problems).

The 3D view can be turned off in [Options](#options), which saves memory. It can also show a board and the footprint's pads. Choosing a higher model detail gives smoother curves, but large models take longer to load.

= Adding parts and tables

# add-elements | Add elements
This adds many similar components at once, differing in their value: resistors of a series, a list of capacitors, and so on. Open it with **Add elements** in the toolbar, with Edit on.

## Step by step
1. In the table, **select a row like the ones you want**. Its fields are the starting point, so the new rows look the same.
2. Open **Add elements**. The **Element** type is chosen from the table's name; change it if needed.
3. Choose the values: **Standard series** with a range, or a **List**.
4. Check the **Fields**: what each new row gets.
5. Check **Rows**, the preview of what will be added.
6. Press **Add rows**. They are added as one step of [Undo](#editing), and they aren't in the database until you save.

## Values
**Standard series** makes the values of a series between From and To: E3 to E192, R10, or 1-2-5. **List** takes values you type, one per line or separated by commas, and ready lists of the type's usual values.

Values can be written the way you are used to:

| Written as | Means |
| `4K7`, `4.7k`, `4,7 kOhm` | 4700 Ohm |
| `0R56`, `R47` | 0.56 Ohm, 0.47 Ohm |
| `100n`, `4u7`, `33UF` | 100 nF, 4.7 uF, 33 uF |
| `3V3` | 3.3 V |
| `16 MHz` | 16 MHz |

A number alone is in the type's usual unit: `100` is 100 pF for a ceramic capacitor. In `M` and `m`, the type decides: a resistor's `1M` is a megaohm, `5m` is 5 milliohm, and an inductor's `1MH` means millihenry.

## Field texts
Each field has a text that every new row gets. Placeholders in it are replaced in each row:

| Placeholder | For 4K7 it gives |
| `{Value}` | 4K7 |
| `{VALUE}` | 4K7 in capitals |
| `{Text}` | 4.7 kOhm |
| `{Short}` | The value, written short |
| `{N}` | The row's number |

The window finds the selected row's value in its fields and puts placeholders there for you: `RES 0R56 0.1%` becomes `RES {Value} 0.1%`. The line under the fields shows what they stand for with the current type.

Each new row needs its own Part Number, so that field should contain a placeholder. Rows that would repeat one are left out and marked in **Rows**. If a field can't take a text, the problem is shown and **Add rows** stays off.

## Element types
A type holds the unit, how values are written, the series and lists, and the field texts. There are 27 standard types, and each is a `.json` file you can change or share.

| Button | What it does |
| **Save as** | Keeps the current values and field texts as a type of its own |
| **Export, Import** | Writes a type to a file, or adds one from a file |
| **Folder** | Opens the folder of the type files |

# new-tables | Creating tables
**File > New table** (`Ctrl+N`) adds a table to the open database.

1. Give the table a name.
2. Under **Start from**, choose where its fields come from: the minimal set, an empty table, or a [template](#templates).
3. Change the fields as needed.
4. Press **Create table**.

## Fields
| Column | Meaning |
| **Name** | The field's name. Access doesn't allow some characters, and the window tells you which. |
| **Type** | **Text** for almost everything: Altium reads every field as text. **Long text** for values over 255 characters, such as long URLs. |
| **Size** | The maximum length of a Text field, from 1 to 255. |
| **Required** | Access won't save a row with this field empty. |
| **Key** | The primary key: unique and required. Put it on Part Number. |

**Add field** (`Ctrl+Enter`) adds a field after the selected one, and **Standard fields** adds the usual Altium fields. `Alt+Up` and `Alt+Down` move a field, or drag it by its handle.

> Altium needs at least Part Number, Library Ref, Library Path, Footprint Ref and Footprint Path to place a component. The Minimal start has them.

**Save fields as template** keeps the fields to make more tables like this one.

# templates | Table templates
A template is a set of fields, ready for making new tables. Manage them in **Tools > Table templates**.

## Making a template
- With the **+** button in the Templates window.
- From an existing table: right-click it in the table list and choose **Save as template**.
- From the New table window: **Save fields as template**.
- With the **Duplicate** button, to start from another template.

Name, description and fields are changed on the right, and saved when you pick another template or close the window. The app starts with the **Altium component** template: Part Number, symbol, three footprints, description, manufacturer, supplier and datasheet links. If you delete it, [Options](#options) can add it again.

## Sharing templates
Each template is one `.json` file. **Export** saves one as a file, **Import** adds some from files, and **Open folder** shows where they are.

> To give a whole team the same templates, choose a shared folder (network drive, OneDrive, a Git repository) in [Options](#options), under Templates. When you switch folders, you can copy your templates to the new one.

## What New table starts with
Options also sets what the New table window shows first: the choice used last time, Minimal, an empty table, or a template.

# deleted-tables | Deleted tables
Deleting a table removes it and all its rows from the database, after asking you. Before that, the app keeps a copy of it, so a mistake can be undone. Open them in **Tools > Deleted tables**.

| Button | What it does |
| **Restore** | Puts the table back into the open database, with its key and AutoNumber. |
| **Delete for good** | Removes this copy. |
| **Open in Access** | Opens the copy, to look at it or take rows from it. |
| **Open folder** | Shows where the copies are. |

The 30 newest copies are kept. Unlike changes to rows, deleting a table takes effect at once, and `Ctrl+Z` doesn't bring it back: use Restore.

= Settings and help

# options | Options
**Tools > Options** (`Ctrl+,`). **Apply** shows the changes at once, **OK** keeps them, and **Cancel** drops what wasn't applied. **Restore defaults** puts the options back; templates, remembered column widths and search columns stay.

| Page | What you can set |
| **General** | Reopening the last database and its tables; temporary tabs. |
| **Appearance** | Theme, interface scale, footprint background. |
| **Tables** | Row height, text size, column widths, Details panel, grid lines, row shading, keeping the first column in place. |
| **Editing** | Turning Edit on when a table opens, asking before deleting rows, how many changes Undo keeps. |
| **Preview** | The 3D view: board, pads, model detail. |
| **Templates** | Templates folder, what New table starts with, the built-in template. |

Click anywhere on a slider's bar to move it there, or use the arrow keys for one step at a time.

## Useful ones
- **Shade every other row** makes long rows easier to follow.
- **Turn on Edit when a table opens** suits days of editing. Temporary tabs stay read-only.
- **Ask before deleting rows** adds a question, if you prefer one.

# shortcuts | Keyboard shortcuts
## Everywhere
| Keys | Action |
| `F1` | Help |
| `Ctrl+O` | Open database |
| `Ctrl+N` | New table |
| `Ctrl+,` | Options |
| `Ctrl+1` to `Ctrl+4` | Layouts |
| `Ctrl+F` | Search rows, or filter tables when in the database panel |

## Tabs
| Keys | Action |
| `Ctrl+W`, `Ctrl+F4` | Close the tab |
| `Ctrl+Tab`, `Ctrl+Shift+Tab` | Next, previous tab |
| `Ctrl+Page Down`, `Ctrl+Page Up` | Next, previous tab |
| `Alt+1` to `Alt+8` | Go to that tab |
| `Alt+9` | Go to the last tab |

## Tables
| Keys | Action |
| `Ctrl+E` | Turn Edit on or off |
| `Ctrl+S` | Save the table |
| `Ctrl+Shift+S` | Save all tables |
| `Ctrl+Z` | Undo |
| `Ctrl+Y`, `Ctrl+Shift+Z` | Redo |
| `F5` | Read the table again |
| `Ctrl+Plus` | Add row |
| `Ctrl+D` | Duplicate row |
| `Ctrl+Minus` | Delete rows |
| `Ctrl+C`, `Ctrl+X`, `Ctrl+V` | Copy, cut, paste cells |
| `Del` | Clear cells |
| `Enter` | End the row being typed |
| `Esc` | Take back the row being typed |

## Search box
| Keys | Action |
| `Up`, `Down` | Move through the rows |
| `Enter` | Go to the table |
| `Esc` | Clear the search |

## Details
| Keys | Action |
| `Ctrl` + click | Open a link or file |
| `Enter` | Apply the value |
| `Esc` | Drop the value |

## Field lists in New table and Templates
| Keys | Action |
| `Ctrl+Enter` | Add a field |
| `Alt+Up`, `Alt+Down` | Move the field |

# problems | Solving problems
## The database doesn't open
The message tells the reason. If the Microsoft Access Database Engine is missing, it says what to install: see [Databases and tables](#databases). A file open in Access can usually still be read, but a file set to read-only can't be saved to.

## The symbol or footprint is empty
- **Component not found in this library** means the library was found, but has no such Library Ref or Footprint Ref. Check the spelling, or pick it with the button in [Details](#details).
- If nothing is shown, the library file wasn't found. Check Library Path or Footprint Path: the file may have moved. Files are also looked for in the folders next to the database.

## The 3D view is empty
The row's footprint may have no 3D model. The 3D view also needs Microsoft Edge WebView2, which comes with Windows 11. Check that the view is on in [Options](#options).

## A change can't be kept
- **Another row already has the Part Number**: see [Part Numbers](#editing).
- **takes at most N characters**: the text is longer than the field. Shorten it, or change the field in Access.
- **takes a number** or **a date**: the field only holds that kind of value.

## Save refuses
Nothing was written, and your changes are still in the table. The message names the row.

- **The row isn't in the database as shown any more**: someone changed or deleted it in Access or Altium. Press `F5`, choosing whether to save or drop your changes.
- **a row in the database already has the Part Number**: it was added elsewhere in the meantime.
- In a table without a primary key, rows that differ only in long text fields can't be told apart. Change them in Access, or add a key.

## My settings went back to the defaults
The settings file was damaged. The old one is kept next to it as `settings.json.bad`.

## Files of the app
Everything is in the folder below, which **Help > About** opens for you. Your databases are never stored there.

`%LOCALAPPDATA%\Altium Library Manager`

| Item | What it holds |
| `settings.json` | Options, window and layout |
| `Templates` | Table templates, unless you chose another folder |
| `Element types` | The types of Add elements |
| `Deleted tables` | Copies of deleted tables |
| `errors.log` | Unexpected errors, if there were any. It helps to find out what happened. |
